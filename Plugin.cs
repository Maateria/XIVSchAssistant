using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.DutyState;
using Dalamud.Hooking;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Application.Network;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.GameFonts;
using Dalamud.Interface.ManagedFontAtlas;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;

namespace XIVSchAssitant;

public sealed class Plugin : IDalamudPlugin
{
    private const uint   ScholarJobId      = 28;
    private const uint   SummonEosActionId = 17215;
    private const double PlaceEosDelay     = 3.0;

    // Action "Se placer" : type=11 (pet action), id=3
    private const uint PlacePetActionId = 3;
    // Action "Attendre" / "Stay" : id=2 dans la feuille PetAction
    private const uint StayPetActionId  = 2;

    // Status effect "Biolysis" (Loi de l'infection) applique sur l'ennemi — ID 1895.
    private const uint BiolysisStatusId = 1895;

    // Spells pour l'alerte compte a rebours cooldown
    private const uint ChainStratagemActionId   = 7436;  // Stratagemes Entrelaces (~120s CD)

    // ── WinMM PlaySound (alerte sonore DoT) ──────────────────────────────────
    // Lecture WAV asynchrone via l'API Windows, sans dependance externe.
    [DllImport("winmm.dll", CharSet = CharSet.Auto)]
    private static extern bool PlaySound(string? pszSound, nint hmod, uint fdwSound);
    private const uint SND_FILENAME  = 0x00020000; // pszSound est un chemin de fichier
    private const uint SND_ASYNC     = 0x0001;     // lecture non bloquante

    // Seuil de distance (metres) en dessous duquel Eos est consideree "au centre"
    private const float PlacementThreshold = 3f;

    // Exceptions d'arene : centre different de (100, 100) pour certains territoires
    private static readonly Dictionary<uint, (float X, float Z)> ArenaExceptions = new();

    // ── Hook ZoneClient.SendPacket ────────────────────────────────────────────
    // Intercepte les paquets sortants pour injecter les coordonnees du centre
    // dans le paquet "Se placer" (opcode=0x0030, actionId=3).
    // Le serveur recoit ainsi des coordonnees de destination explicites — equivalent
    // a un clic reticule au sol sur le centre de l'arene.
    private unsafe delegate bool SendPacketDelegate(
        ZoneClient* self, nint packet, uint a3, uint a4, bool a5);
    private Hook<SendPacketDelegate>? _sendPacketHook;

    // ── Services Dalamud ──────────────────────────────────────────────────────
    [PluginService] internal IDalamudPluginInterface  PluginInterface { get; init; } = null!;
    [PluginService] internal IClientState             ClientState     { get; init; } = null!;
    [PluginService] internal ICondition               Condition       { get; init; } = null!;
    [PluginService] internal IFramework               Framework       { get; init; } = null!;
    [PluginService] internal IPluginLog               Log             { get; init; } = null!;
    [PluginService] internal IDataManager             Data            { get; init; } = null!;
    [PluginService] internal IGameInteropProvider     GameInterop     { get; init; } = null!;
    [PluginService] internal ITargetManager           TargetMgr       { get; init; } = null!;
    [PluginService] internal IBuddyList               BuddyList       { get; init; } = null!;
    [PluginService] internal IDutyState               DutyState       { get; init; } = null!;

    internal Configuration Config { get; }

    // ── Etat global ───────────────────────────────────────────────────────────
    // Vrai quand un DutyWiped vient d'etre detecte — en attente que le joueur
    // soit debout et hors chargement pour invoquer Eos.
    private bool      _pendingEosSummonAfterWipe;
    private DateTime? scheduledPlaceEos;
    // Envoie "Attendre" 2s apres chaque "Se placer" pour verrouiller Eos au centre.
    // Independant du check periodique pour garantir l'envoi meme si nextPeriodicCheck
    // est court-circuite par DetectPetEntityChange ou l'acquisition de cible.
    private DateTime? scheduledStayEos;
    // Verification differee qu'Eos est invoquee (login, swap job, entree en instance).
    // Utilise un delai pour laisser le jeu finir son chargement avant de tenter l'invocation.
    private DateTime? scheduledEosCheck;
    // Pour le swap de job : reproduction exacte de PandorasBox/AutoFairy.
    // Delai initial 100ms → chaque frame : BuddyList.PetBuddy?/GetActionStatus==0 → UseAction.
    // Timeout 5s. Pas de delai fixe : invocation au plus tot que le jeu le permet.
    private bool     _waitingForAmReady;    // true = poller AM chaque frame
    private DateTime _waitingForAmStart;    // ne commencer a poller qu'apres ce temps
    private DateTime _amReadyTimeout;       // abandon apres cette date
    private uint      currentJobId;
    private bool      isInEightPlayerContent;
    private DateTime  nextPeriodicCheck = DateTime.MinValue;

    // ── Etat placement ────────────────────────────────────────────────────────
    // Vrai quand un DoPlaceEos doit s'executer au prochain tick Framework.Update.
    private bool _pendingPlaceEos;

    // ── Injection coords dans le paquet Se placer ─────────────────────────────
    // Quand _interceptNextPlacePacket est vrai, le prochain paquet
    // opcode=0x0030 + actionId=3 voit ses coords (+0x34 X / +0x38 Y / +0x3C Z)
    // remplacees par _pendingPlaceCenter avant transmission au serveur.
    private bool    _interceptNextPlacePacket;
    private Vector3 _pendingPlaceCenter;

    // ── Verrou "Attendre" ─────────────────────────────────────────────────────
    // Cooldown 25s pour eviter le spam de la commande "Attendre <me>".
    private DateTime _lastStaySentAt = DateTime.MinValue;

    // ── Detection changements d'entite ────────────────────────────────────────
    private uint _lastTargetId;      // EntityId de la derniere cible joueur
    private uint _lastEosPetEntityId; // EntityId du dernier pet detecte (Eos/Seraph)

    // ── Noms localises des actions pet ───────────────────────────────────────
    // Charges depuis la feuille Excel "PetAction" dans la langue du client au demarrage.
    // Utilises dans SendPlaceChatCommand / SendStayCommand pour que les commandes
    // /petaction fonctionnent quelle que soit la langue du jeu (EN/FR/DE/JP).
    private string _placePetActionName = "Se placer"; // fallback FR
    private string _stayPetActionName  = "Attendre";  // fallback FR

    // ── Alerte DoT Biolysis ───────────────────────────────────────────────────
    private bool     _biolysisWasActive;            // etat du DoT au tick precedent
    private DateTime _lastDotAlertAt = DateTime.MinValue; // cooldown anti-spam
    private string?  _dotSoundPath;                 // chemin vers Chocobo.wav

    // ── Overlay compte a rebours local ───────────────────────────────────────
    // Affiche un grand chiffre centre a l'ecran (5, 4, 3, 2, 1) avec son natif.
    // Entierement local — aucun paquet reseau envoye.
    // Police haute resolution chargee via FontAtlas pour eviter le pixelise.
    private IFontHandle? _countdownFont;
    private bool     _localCountdownActive;
    private float    _localCountdownStartValue;
    private DateTime _localCountdownStart;
    private int      _localCountdownLastTick;   // dernier entier affiche (evite bips doubles)

    // ── Etat alerte cooldown ──────────────────────────────────────────────────
    private bool _csAlertFired;  // alerte CS deja declenchee pour le CD en cours

    // ─────────────────────────────────────────────────────────────────────────

    public Plugin()
    {
        Log.Information("[XIVSchAssitant] Demarrage...");
        Config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        isInEightPlayerContent = IsEightPlayerContent(ClientState.TerritoryType);

        unsafe
        {
            var ps = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState.Instance();
            if (ps != null) currentJobId = ps->CurrentClassJobId;

            _sendPacketHook = GameInterop.HookFromAddress<SendPacketDelegate>(
                (nint)ZoneClient.MemberFunctionPointers.SendPacket,
                OnSendPacket);
            _sendPacketHook.Enable();
        }

        PluginInterface.UiBuilder.Draw += OnDraw;
        // Police haute resolution pour le countdown — TrumpGothicE est le
        // font "grand format" du jeu (barres HP, titres), ideal pour des chiffres.
        _countdownFont = PluginInterface.UiBuilder.FontAtlas.NewGameFontHandle(
            new GameFontStyle(GameFontFamily.TrumpGothic, 96f));

        ClientState.Login            += OnLogin;
        ClientState.ClassJobChanged  += OnClassJobChanged;
        ClientState.TerritoryChanged += OnTerritoryChanged;
        Framework.Update             += OnFrameworkUpdate;
        DutyState.DutyWiped          += OnDutyWiped;

        // Charger les noms localises des actions pet depuis les donnees Excel du jeu.
        LoadPetActionNames();

        // Chemin du fichier audio — copie dans le dossier du plugin via le .csproj.
        _dotSoundPath = Path.Combine(
            PluginInterface.AssemblyLocation.Directory!.FullName,
            "sound", "Chocobo.wav");
        if (!File.Exists(_dotSoundPath))
            Log.Warning($"[XIVSchAssitant] Son introuvable : {_dotSoundPath}");
        else
            Log.Information($"[XIVSchAssitant] Son charge : {_dotSoundPath}");

        Log.Information("[XIVSchAssitant] Charge — hook ZoneClient.SendPacket actif.");
    }

    // ── Evenements ────────────────────────────────────────────────────────────

    // Declenche quand tout le groupe est mort et que l'ecran passe au noir.
    // Ne fire PAS sur un rez individuel en combat → detection fiable du wipe global.
    private void OnDutyWiped(IDutyStateEventArgs args)
    {
        if (!Config.Enabled) return;
        if (currentJobId != ScholarJobId) return;
        Log.Information("[XIVSchAssitant] DutyWiped — Eos sera invoquee apres resurrection.");
        _pendingEosSummonAfterWipe = true;
    }

    private void OnTerritoryChanged(uint territoryType)
    {
        isInEightPlayerContent = IsEightPlayerContent(territoryType);
        Log.Debug($"[XIVSchAssitant] Territoire {territoryType} — 8j : {isInEightPlayerContent}");
        // Delai plus long pour laisser le temps au cercle bleu de se resoudre
        // avant que le check periodique ne tente de repositionner Eos.
        if (isInEightPlayerContent) nextPeriodicCheck = DateTime.UtcNow.AddSeconds(15);

        // Verifier qu'Eos est invoquee dans tout contenu instancie (donjon, trial, raid...).
        // Delai de 5s pour laisser l'instance finir de charger avant de tenter l'invocation.
        if (Config.Enabled && IsAnyInstanceContent(territoryType))
        {
            Log.Debug($"[XIVSchAssitant] Entree instance — verification Eos dans 5s.");
            scheduledEosCheck = DateTime.UtcNow.AddSeconds(5);
        }
    }

    private void OnClassJobChanged(uint classJobId)
    {
        currentJobId = classJobId;
        if (!Config.Enabled) return;
        // Ne pas declencher pendant un changement de zone (identique a PandorasBox).
        if (Condition[ConditionFlag.BetweenAreas]) return;
        if (classJobId == ScholarJobId)
        {
            Log.Information("[XIVSchAssitant] Swap vers Erudit — demarrage polling AM.");
            _waitingForAmReady = true;
            _waitingForAmStart = DateTime.UtcNow.AddMilliseconds(100); // delai initial 100ms (PandorasBox default ThrottleF)
            _amReadyTimeout    = DateTime.UtcNow.AddSeconds(5);        // timeout 5s (PandorasBox EnqueueWithTimeout)
        }
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!Config.Enabled) return;
        if (!ClientState.IsLoggedIn) return;

        // ── Post-wipe : attente resurrection ─────────────────────────────────────
        // DutyWiped a ete detecte (OnDutyWiped) — on attend que le joueur soit
        // debout et hors chargement (Unconscious=false, BetweenAreas*=false)
        // avant de planifier l'invocation d'Eos.
        if (_pendingEosSummonAfterWipe
            && !Condition[ConditionFlag.Unconscious]
            && !Condition[ConditionFlag.BetweenAreas]
            && !Condition[ConditionFlag.BetweenAreas51])
        {
            _pendingEosSummonAfterWipe = false;
            Log.Information("[XIVSchAssitant] Post-wipe — joueur operationnel, invocation Eos dans 2.5s.");
            scheduledEosCheck = DateTime.UtcNow.AddSeconds(2.5);
        }

        // ── Verification Eos invoquee (login / swap job / entree instance) ───────
        if (scheduledEosCheck.HasValue && DateTime.UtcNow >= scheduledEosCheck.Value)
        {
            scheduledEosCheck = null;
            CheckAndSummonEos();
        }

        // ── Placement planifie (apres invocation) ─────────────────────────────
        if (scheduledPlaceEos.HasValue && DateTime.UtcNow >= scheduledPlaceEos.Value)
        {
            scheduledPlaceEos = null;
            TryPlaceEos();
        }

        // ── Attendre planifie (apres Se placer) ───────────────────────────────
        // Verrouille Eos au centre 2s apres chaque placement.
        if (scheduledStayEos.HasValue && DateTime.UtcNow >= scheduledStayEos.Value)
        {
            scheduledStayEos = null;
            SendStayCommand();
            _lastStaySentAt = DateTime.UtcNow;
        }

        // ── Placement en attente ──────────────────────────────────────────────
        // Execute ici (et non dans TryPlaceEos) pour rester sur le thread principal
        // Dalamud, hors de tout contexte de callback d'evenement.
        if (_pendingPlaceEos)
        {
            _pendingPlaceEos = false;
            DoPlaceEos();
        }

        // ── Detection acquisition de cible ────────────────────────────────────
        // Quand le joueur cible le boss pour la premiere fois, "Se placer <t>"
        // devient disponible — on declenche un repositionnement immediat.
        {
            uint newTargetId = TargetMgr.Target != null ? TargetMgr.Target.EntityId : 0u;
            if (newTargetId != _lastTargetId)
            {
                bool wasNoTarget = _lastTargetId == 0;
                _lastTargetId = newTargetId;
                if (wasNoTarget && newTargetId != 0
                    && isInEightPlayerContent && currentJobId == ScholarJobId)
                {
                    Log.Information(
                        $"[XIVSchAssitant] [TargetChange] Cible acquise (0x{newTargetId:X8}) " +
                        "— repositionnement immediat.");
                    nextPeriodicCheck = DateTime.UtcNow;
                }
            }
        }

        // ── Attente ActionManager pret (swap de job vers Erudit) ─────────────
        // Reproduction exacte de PandorasBox/AutoFairy TrySummon + TaskManager :
        //   1. Attente initiale 100ms (_waitingForAmStart)
        //   2. Chaque frame : BuddyList.PetBuddy != null → deja la, stop
        //   3.                GetActionStatus(SummonEos) != 0 → pas pret, retry
        //   4.                GetActionStatus(SummonEos) == 0 → UseAction, stop
        //   5. Timeout 5s → abandon
        if (_waitingForAmReady && currentJobId == ScholarJobId)
        {
            if (DateTime.UtcNow < _waitingForAmStart)
            {
                // Attente initiale : ne rien faire (equiv. TaskManager.EnqueueDelay)
            }
            else if (DateTime.UtcNow >= _amReadyTimeout)
            {
                _waitingForAmReady = false;
                Log.Warning("[XIVSchAssitant] Timeout attente AM — abandon.");
            }
            else if (BuddyList.PetBuddy != null)
            {
                // Pet deja present (equiv. TrySummon return true quand PetBuddy != null)
                _waitingForAmReady = false;
                Log.Debug("[XIVSchAssitant] AM polling — pet deja present.");
            }
            else
            {
                unsafe
                {
                    var am = ActionManager.Instance();
                    if (am != null &&
                        am->GetActionStatus(ActionType.Action, SummonEosActionId, 0xE0000000UL, true, true) == 0)
                    {
                        // AM pret : equiv. TrySummon → UseAction → return true
                        _waitingForAmReady = false;
                        am->UseAction(ActionType.Action, SummonEosActionId);
                        Log.Information("[XIVSchAssitant] AM pret — Eos invoquee.");
                        if (isInEightPlayerContent)
                        {
                            scheduledPlaceEos = DateTime.UtcNow.AddSeconds(PlaceEosDelay);
                            Log.Information($"[XIVSchAssitant] Placement planifie dans {PlaceEosDelay}s.");
                        }
                    }
                }
            }
        }

        // ── Detection swap Seraph / Eos ───────────────────────────────────────
        // Chaque frame : detecte tout changement d'entite pet (Dissipation, Seraphim).
        if (isInEightPlayerContent && currentJobId == ScholarJobId)
            DetectPetEntityChange();

        // ── Verification periodique ───────────────────────────────────────────
        if (isInEightPlayerContent && currentJobId == ScholarJobId
            && DateTime.UtcNow >= nextPeriodicCheck)
        {
            CheckAndRepositionEos();
        }

        // ── Alerte DoT Biolysis (Loi de l'infection) ─────────────────────────
        // Detecte quand le DoT 30s tombe du boss et joue un son d'alerte.
        // Actif uniquement si Scholar + en combat + option activee.
        if (Config.DotAlertEnabled && currentJobId == ScholarJobId
            && Condition[ConditionFlag.InCombat])
        {
            if (TargetMgr.Target is IBattleChara targetChara)
            {
                unsafe
                {
                    var playerObj = GameObjectManager.Instance()->Objects.IndexSorted[0].Value;
                    if (playerObj != null)
                    {
                        // Cherche notre Biolysis sur la cible (SourceId == notre EntityId)
                        bool biolysisActive = false;
                        var localId = playerObj->EntityId;
                        foreach (var s in targetChara.StatusList)
                        {
                            if (s.StatusId == BiolysisStatusId && s.SourceId == localId)
                            { biolysisActive = true; break; }
                        }

                        // Transition actif → inactif : le DoT vient de tomber
                        if (_biolysisWasActive && !biolysisActive)
                            PlayDotAlert();

                        _biolysisWasActive = biolysisActive;
                    }
                }
            }
            else
            {
                // Pas de cible valide (boss intangible, phase...) : reset sans alerter
                _biolysisWasActive = false;
            }
        }
        else
        {
            _biolysisWasActive = false;
        }

        // ── Alerte cooldown Chain Stratagem / Emergency Tactics ───────────────────
        // 5 secondes avant que le skill revienne, declenche un compte a rebours natif
        // local (non diffuse au groupe — ecriture directe dans la structure du timer).
        // Pas de guard InCombat : on veut l'alerte meme entre deux pulls.
        if (Config.CooldownAlertEnabled && currentJobId == ScholarJobId)
            CheckCooldownAlert();

        // ── Tick overlay compte a rebours ─────────────────────────────────────────
        // Joue le son natif (UIGlobals) a chaque nouveau chiffre entier.
        // L'affichage est gere dans OnDraw (UiBuilder.Draw).
        if (_localCountdownActive)
        {
            float elapsed    = (float)(DateTime.UtcNow - _localCountdownStart).TotalSeconds;
            float remaining  = _localCountdownStartValue - elapsed;
            if (remaining <= 0f)
            {
                _localCountdownActive = false;
            }
            else
            {
                int tick = (int)Math.Ceiling(remaining);
                if (tick < _localCountdownLastTick)
                {
                    _localCountdownLastTick = tick;
                    unsafe { UIGlobals.PlaySoundEffect(48); }
                }
            }
        }
    }

    // ── Invocation d'Eos ──────────────────────────────────────────────────────

    private unsafe void TrySummonEos()
    {
        Log.Information("[XIVSchAssitant] Invocation d'Eos...");
        ActionManager.Instance()->UseAction(ActionType.Action, SummonEosActionId);
        if (isInEightPlayerContent)
        {
            scheduledPlaceEos = DateTime.UtcNow.AddSeconds(PlaceEosDelay);
            Log.Information($"[XIVSchAssitant] Placement planifie dans {PlaceEosDelay}s.");
        }
    }

    // Appele quand le joueur se connecte sur son personnage.
    // Necessaire pour invoquer Eos si le perso est deja en Erudit au login.
    private void OnLogin()
    {
        Log.Information("[XIVSchAssitant] Connexion detectee — verification Eos dans 5s.");
        // Delai de 5s : le perso n'est pas completement charge au moment de l'evenement.
        scheduledEosCheck = DateTime.UtcNow.AddSeconds(5);
    }

    // Verifie qu'Eos est invoquee et l'invoque si absente.
    // Appele par les chemins differee (login, entree instance).
    // Utilise BuddyList.PetBuddy comme PandorasBox/AutoFairy.
    private unsafe void CheckAndSummonEos()
    {
        if (!Config.Enabled) return;
        if (!ClientState.IsLoggedIn) return;

        // Re-lire le job depuis PlayerState UNIQUEMENT si currentJobId n'est pas
        // encore initialise (cas du login quand le plugin charge avant la connexion).
        if (currentJobId == 0)
        {
            var ps = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState.Instance();
            if (ps != null) currentJobId = ps->CurrentClassJobId;
        }

        if (currentJobId != ScholarJobId) return;

        // BuddyList.PetBuddy : API Dalamud, equivalent de Svc.Buddies.PetBuddy (PandorasBox).
        if (BuddyList.PetBuddy != null)
        {
            Log.Debug("[XIVSchAssitant] [EosCheck] Pet deja present.");
            return;
        }

        Log.Information("[XIVSchAssitant] [EosCheck] Pet absent — invocation.");
        TrySummonEos();
        if (!scheduledEosCheck.HasValue)
            scheduledEosCheck = DateTime.UtcNow.AddSeconds(1);
    }

    // ── Placement d'Eos ───────────────────────────────────────────────────────

    private void TryPlaceEos()
    {
        if (BuddyList.PetBuddy == null)
        {
            Log.Warning("[XIVSchAssitant] TryPlaceEos — pet non trouve.");
            return;
        }
        // Ne pas envoyer la commande pendant le chargement ou le cercle bleu
        // (phase "instance chargee, pas encore demarree").
        // BetweenAreas   = ecran de chargement principal.
        // BetweenAreas51 = etat secondaire couvrant la phase cercle bleu / ready-check.
        if (Condition[ConditionFlag.BetweenAreas] || Condition[ConditionFlag.BetweenAreas51])
        {
            Log.Debug("[XIVSchAssitant] TryPlaceEos — chargement/cercle bleu detecte, retry dans 2s.");
            if (!scheduledPlaceEos.HasValue)
                scheduledPlaceEos = DateTime.UtcNow.AddSeconds(2.0);
            return;
        }
        _pendingPlaceEos = true;
    }

    // Envoie "/petaction Se placer" avec injection des coordonnees du centre dans
    // le paquet reseau resultant. Appele depuis Framework.Update (_pendingPlaceEos).
    private unsafe void DoPlaceEos()
    {
        var center = GetArenaCenter();

        // Activer l'injection AVANT d'envoyer la commande chat.
        // OnSendPacket intercepte le paquet (opcode=0x0030, actionId=3) dans les ~4ms
        // et remplace les coords par celles du centre : le serveur place Eos exactement
        // au centre, sans passer par le reticule de ciblage au sol.
        _pendingPlaceCenter       = center;
        _interceptNextPlacePacket = true;
        SendPlaceChatCommand();
        // Planifier "Attendre" 2s apres le placement pour verrouiller Eos au centre.
        // Evite qu'elle reparte en mode "suivre" entre le placement et le prochain check periodique.
        scheduledStayEos = DateTime.UtcNow.AddSeconds(2.0);
    }

    // ── Commandes chat ────────────────────────────────────────────────────────

    private unsafe void SendPlaceChatCommand()
    {
        var uiModule = FFXIVClientStructs.FFXIV.Client.UI.UIModule.Instance();
        if (uiModule == null) return;
        // Preferer <t> (boss) pour annuler le mode Heel serveur.
        // Fallback <me> si aucune cible (debut d'instance, hors combat).
        if (TargetMgr.Target != null)
        {
            Log.Information($"[XIVSchAssitant] [ChatCmd] {_placePetActionName} <t>");
            var msg = new FFXIVClientStructs.FFXIV.Client.System.String.Utf8String(
                $@"/petaction ""{_placePetActionName}"" <t>");
            uiModule->ProcessChatBoxEntry(&msg, 0, false);
        }
        else
        {
            Log.Information($"[XIVSchAssitant] [ChatCmd] {_placePetActionName} <me>");
            var msg = new FFXIVClientStructs.FFXIV.Client.System.String.Utf8String(
                $@"/petaction ""{_placePetActionName}"" <me>");
            uiModule->ProcessChatBoxEntry(&msg, 0, false);
        }
    }

    private unsafe void SendStayCommand()
    {
        var uiModule = FFXIVClientStructs.FFXIV.Client.UI.UIModule.Instance();
        if (uiModule == null) return;
        // "Attendre" sans cible = rester en place.
        // Avec <me> le jeu interpretait ca comme "rejoindre le joueur et rester la"
        // ce qui causait un aller-retour centre ↔ joueur en boucle.
        Log.Information($"[XIVSchAssitant] [ChatCmd] {_stayPetActionName}");
        var msg = new FFXIVClientStructs.FFXIV.Client.System.String.Utf8String(
            $@"/petaction ""{_stayPetActionName}""");
        uiModule->ProcessChatBoxEntry(&msg, 0, false);
    }

    // ── Hook ZoneClient.SendPacket ────────────────────────────────────────────
    // Intercepte les paquets opcode=0x0030 / actionId=3 (Se placer).
    // Structure du paquet (commande pet, taille stockee a +0x00) :
    //   +0x00 : uint  taille totale du buffer (ex: 298 = 0x12A)
    //   +0x08 : ushort opcode
    //   +0x24 : uint  actionId (3 = Se placer)
    //   +0x34 : float X destination
    //   +0x38 : float Y destination (sol de l'arene = 0)
    //   +0x3C : float Z destination
    // NB : a3=0 pour les commandes pet (la taille reelle est a +0x00, pas dans a3).
    //      a5=True pour "Attendre" uniquement (Se placer a a5=False).

    private unsafe bool OnSendPacket(
        ZoneClient* self, nint packet, uint a3, uint a4, bool a5)
    {
        if (_interceptNextPlacePacket && packet != nint.Zero)
        {
            try
            {
                ushort opcode = *(ushort*)(packet + 0x08);
                if (opcode == 0x0030 && !a5)
                {
                    uint headerSize = *(uint*)(packet + 0x00);
                    if (headerSize >= 0x40)
                    {
                        uint pktActionId = *(uint*)(packet + 0x24);
                        if (pktActionId == PlacePetActionId)
                        {
                            float origX = *(float*)(packet + 0x34);
                            float origZ = *(float*)(packet + 0x3C);
                            *(float*)(packet + 0x34) = _pendingPlaceCenter.X;
                            *(float*)(packet + 0x38) = 0f;   // sol de l'arene
                            *(float*)(packet + 0x3C) = _pendingPlaceCenter.Z;
                            _interceptNextPlacePacket = false;
                            Log.Information(
                                $"[XIVSchAssitant] [PacketInject] " +
                                $"({origX:F2},_,{origZ:F2}) → " +
                                $"({_pendingPlaceCenter.X:F2},0,{_pendingPlaceCenter.Z:F2})");
                        }
                    }
                }
            }
            catch { /* ne pas crasher sur pointeur invalide */ }
        }

        return _sendPacketHook!.Original(self, packet, a3, a4, a5);
    }

    // ── Detection swap Seraph / Eos ───────────────────────────────────────────
    // Compare l'EntityId du pet actuel avec le dernier connu.
    // Sur changement : declenche immediatement un repositionnement (~16ms de latence).

    private unsafe void DetectPetEntityChange()
    {
        var playerObj = GameObjectManager.Instance()->Objects.IndexSorted[0].Value;
        if (playerObj == null) return;
        uint playerId = playerObj->EntityId;

        GameObject* petObj = null;
        for (var i = 1; i < 200; i++)
        {
            var o = GameObjectManager.Instance()->Objects.IndexSorted[i].Value;
            if (o == null) continue;
            if (o->OwnerId == playerId && o->ObjectKind == ObjectKind.BattleNpc)
            { petObj = o; break; }
        }

        uint newPetId = petObj != null ? petObj->EntityId : 0u;
        if (newPetId == _lastEosPetEntityId) return;

        uint oldPetId = _lastEosPetEntityId;
        _lastEosPetEntityId = newPetId;

        if (newPetId != 0)
        {
            Log.Information(
                $"[XIVSchAssitant] [PetChange] 0x{oldPetId:X8}→0x{newPetId:X8} " +
                "— repositionnement immediat.");
            nextPeriodicCheck = DateTime.UtcNow;
        }
    }

    // ── Verification periodique et repositionnement ───────────────────────────

    private unsafe void CheckAndRepositionEos()
    {
        var playerObj = GameObjectManager.Instance()->Objects.IndexSorted[0].Value;
        if (playerObj == null) return;
        var playerId = playerObj->EntityId;

        for (var i = 0; i < 200; i++)
        {
            var obj = GameObjectManager.Instance()->Objects.IndexSorted[i].Value;
            if (obj == null) continue;
            if (obj->OwnerId != playerId || obj->ObjectKind != ObjectKind.BattleNpc) continue;

            float cx = 100f, cz = 100f;
            if (ArenaExceptions.TryGetValue(ClientState.TerritoryType, out var ex))
            { cx = ex.X; cz = ex.Z; }

            float dx   = obj->Position.X - cx;
            float dz   = obj->Position.Z - cz;
            float dist = MathF.Sqrt(dx * dx + dz * dz);

            Log.Debug(
                $"[XIVSchAssitant] Eos @ ({obj->Position.X:F1},{obj->Position.Z:F1}) dist={dist:F1}");

            if (dist <= PlacementThreshold)
            {
                bool inCombat = Condition[ConditionFlag.InCombat];
                nextPeriodicCheck = DateTime.UtcNow.AddSeconds(inCombat ? 2.0 : 5.0);
                // Envoyer "Attendre <me>" periodiquement pour verrouiller Eos au centre.
                // Cooldown 25s.
                if ((DateTime.UtcNow - _lastStaySentAt).TotalSeconds > 25.0)
                {
                    Log.Information("[XIVSchAssitant] [Periodic] Eos au centre — Attendre.");
                    SendStayCommand();
                    _lastStaySentAt = DateTime.UtcNow;
                }
                return;
            }

            // Eos hors centre : repositionnement.
            // Intervalle fixe 5s entre tentatives — evite le spam de commandes.
            // (La detection d'acquisition de cible declenche un check immediat
            //  via nextPeriodicCheck = DateTime.UtcNow dans OnFrameworkUpdate.)
            Log.Debug($"[XIVSchAssitant] Eos hors centre (dist={dist:F1}) — repositionnement.");
            nextPeriodicCheck = DateTime.UtcNow.AddSeconds(5.0);
            // Reset du cooldown "Attendre" pour qu'elle soit envoyee immediatement
            // au prochain check qui trouve Eos au centre — sinon Eos reste en mode
            // "suivre" apres placement et revient vers le joueur des qu'il bouge.
            _lastStaySentAt = DateTime.MinValue;
            TryPlaceEos();
            return;
        }

        // Pet non trouve (Dissipation en cours, etc.) — check rapide a 1s.
        _lastEosPetEntityId = 0;
        nextPeriodicCheck = DateTime.UtcNow.AddSeconds(1);
        Log.Debug("[XIVSchAssitant] Pet non trouve.");
    }

    // ── Noms localises PetAction ──────────────────────────────────────────────

    // Struct minimal compatible Lumina pour lire la feuille "PetAction" brute.
    // La feuille n'existe pas en tant que type genere dans Lumina.Excel.Sheets,
    // mais ExcelModule accepte tout type implementant IExcelRow<T>.
    // Structure de la feuille PetAction :
    //   col 0  Name    string (localise — "Se placer" / "Place" / "Platzieren" / "配置")
    //   col 1  ...     autres colonnes non utilisees ici
    [Sheet("PetAction")]
    private readonly struct PetActionRow(ExcelPage page, uint offset, uint row)
        : IExcelRow<PetActionRow>
    {
        // Membres requis par IExcelRow<T> dans cette version de Lumina
        public uint     RowId      => row;
        public ExcelPage ExcelPage => page;
        public uint     RowOffset  => offset;
        // Colonne 0 : Name — chaine localisee du nom de l'action.
        // ReadString(stringOffset, dataOffset) : lit l'offset de chaine situe
        // a la position stringOffset dans la page, resout la chaine relative a dataOffset.
        // Pour la colonne 0, stringOffset == dataOffset == offset.
        public ReadOnlySeString Name => page.ReadString(offset, offset);
        static PetActionRow IExcelRow<PetActionRow>.Create(
            ExcelPage page, uint offset, uint row) => new(page, offset, row);
    }

    // Charge les noms localises "Se placer" et "Attendre" depuis les donnees
    // Excel du jeu dans la langue du client. Si le chargement echoue (feuille
    // absente, structure differente, etc.), les fallbacks FR restent actifs.
    private void LoadPetActionNames()
    {
        try
        {
            var sheet = Data.GetExcelSheet<PetActionRow>(ClientState.ClientLanguage);
            if (sheet == null)
            {
                Log.Warning("[XIVSchAssitant] PetAction sheet introuvable — fallback FR.");
                return;
            }

            var placeName = sheet.GetRow(PlacePetActionId).Name.ToString();
            if (!string.IsNullOrWhiteSpace(placeName))
                _placePetActionName = placeName;

            var stayName = sheet.GetRow(StayPetActionId).Name.ToString();
            if (!string.IsNullOrWhiteSpace(stayName))
                _stayPetActionName = stayName;

            Log.Information(
                $"[XIVSchAssitant] PetAction localise ({ClientState.ClientLanguage}) : " +
                $"place='{_placePetActionName}', stay='{_stayPetActionName}'");
        }
        catch (Exception ex)
        {
            Log.Warning(
                $"[XIVSchAssitant] PetAction lookup echoue : {ex.Message} " +
                "— fallback FR actif.");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private unsafe GameObject* FindEosObject(uint playerId)
    {
        for (var i = 1; i < 200; i++)
        {
            var obj = GameObjectManager.Instance()->Objects.IndexSorted[i].Value;
            if (obj == null) continue;
            if (obj->OwnerId == playerId && obj->ObjectKind == ObjectKind.BattleNpc)
                return obj;
        }
        return null;
    }

    private bool IsEightPlayerContent(uint territoryType)
    {
        if (territoryType == 0) return false;
        try
        {
            var sheet = Data.GetExcelSheet<ContentFinderCondition>();
            if (sheet == null) return false;
            foreach (var row in sheet)
            {
                try
                {
                    if (row.TerritoryType.RowId == territoryType)
                        return row.ContentType.RowId is 4 or 5;
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            Log.Warning($"[XIVSchAssitant] IsEightPlayerContent: {ex.Message}");
        }
        return false;
    }

    // Retourne true pour tout contenu instancie (donjon, trial, raid, criterion...).
    // Utilise pour decider si on doit verifier la presence d'Eos a l'entree.
    private bool IsAnyInstanceContent(uint territoryType)
    {
        if (territoryType == 0) return false;
        try
        {
            var sheet = Data.GetExcelSheet<ContentFinderCondition>();
            if (sheet == null) return false;
            foreach (var row in sheet)
            {
                try
                {
                    if (row.TerritoryType.RowId == territoryType)
                    {
                        uint ct = row.ContentType.RowId;
                        // ContentType: 2=Donjon, 3=Faille, 4=Trial, 5=Raid, 28=Criterion...
                        // On exclut 0 (monde ouvert) et 6 (PvP).
                        return ct > 0 && ct != 6;
                    }
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            Log.Warning($"[XIVSchAssitant] IsAnyInstanceContent: {ex.Message}");
        }
        return false;
    }

    private unsafe Vector3 GetArenaCenter()
    {
        float x = 100f, z = 100f;
        if (ArenaExceptions.TryGetValue(ClientState.TerritoryType, out var ex))
        { x = ex.X; z = ex.Z; }

        var obj = GameObjectManager.Instance()->Objects.IndexSorted[0].Value;
        float y = obj != null ? obj->Position.Y : 0f;

        return new Vector3(x, y, z);
    }

    // ── Alerte sonore DoT ─────────────────────────────────────────────────────

    private void PlayDotAlert()
    {
        // Cooldown 3s : evite le spam si la detection oscille rapidement
        if ((DateTime.UtcNow - _lastDotAlertAt).TotalSeconds < 3.0) return;
        _lastDotAlertAt = DateTime.UtcNow;
        Log.Debug("[XIVSchAssitant] [DotAlert] Biolysis tombe — lecture son.");
        try
        {
            if (_dotSoundPath != null && File.Exists(_dotSoundPath))
                PlaySound(_dotSoundPath, nint.Zero, SND_FILENAME | SND_ASYNC);
            else
                Log.Warning("[XIVSchAssitant] [DotAlert] Fichier son manquant.");
        }
        catch (Exception ex)
        {
            Log.Warning($"[XIVSchAssitant] [DotAlert] Erreur : {ex.Message}");
        }
    }

    // ── Overlay compte a rebours et alertes cooldown ──────────────────────────

    // Demarre le compte a rebours local : affichage ImGui + son natif chaque seconde.
    // Entierement local — aucun paquet reseau envoye.
    private void TriggerLocalCountdown(float seconds)
    {
        _localCountdownActive    = true;
        _localCountdownStartValue = seconds;
        _localCountdownStart     = DateTime.UtcNow;
        _localCountdownLastTick  = (int)Math.Ceiling(seconds); // ex: 5
        // Son du premier chiffre immediatement
        unsafe { UIGlobals.PlaySoundEffect(48); }
        Log.Information($"[XIVSchAssitant] [CooldownAlert] Compte a rebours {seconds:F1}s (overlay ImGui).");
    }

    // Rendu ImGui : grand chiffre jaune-or dessine directement sur la ForegroundDrawList.
    // Pas de fenetre ImGui — aucun artefact de frame/padding, centrage pixel-perfect.
    private void OnDraw()
    {
        if (!_localCountdownActive) return;
        float elapsed   = (float)(DateTime.UtcNow - _localCountdownStart).TotalSeconds;
        float remaining = _localCountdownStartValue - elapsed;
        if (remaining <= 0f) { _localCountdownActive = false; return; }
        // Police pas encore construite (premier(s) frame) : attendre.
        if (_countdownFont?.Available != true) return;

        int displayNum = (int)Math.Ceiling(remaining);
        var text = displayNum.ToString();
        var io   = ImGui.GetIO();

        using (_countdownFont!.Push())
        {
            var sz  = ImGui.CalcTextSize(text);
            var pos = new Vector2(
                (io.DisplaySize.X - sz.X) * 0.5f,
                io.DisplaySize.Y * 0.38f - sz.Y * 0.5f);
            var dl = ImGui.GetForegroundDrawList();
            // Ombre portee — lisibilite sur fond clair et fond sombre
            dl.AddText(pos + new Vector2(3, 3),
                ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 0.7f)), text);
            // Chiffre jaune-or
            dl.AddText(pos,
                ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.85f, 0.1f, 1f)), text);
        }
    }

    // Verifie CS et ET chaque frame en combat pour detecter la fenetre <=5s.
    private unsafe void CheckCooldownAlert()
    {
        var am = ActionManager.Instance();
        if (am == null) return;
        CheckActionAlert(am, ChainStratagemActionId, ref _csAlertFired, "ChainStratagem");
    }

    // Verifie si <actionId> est a <=5s de revenir et declenche l'alerte si besoin.
    private unsafe void CheckActionAlert(ActionManager* am, uint actionId, ref bool alertFired, string label)
    {
        int group = am->GetRecastGroup((int)ActionType.Action, actionId);
        if (group < 0) return;

        var detail = am->GetRecastGroupDetail(group);
        if (detail == null) return;

        if (!detail->IsActive)
        {
            // Skill disponible → reset le flag pour la prochaine utilisation
            alertFired = false;
            return;
        }

        float remaining = detail->Total - detail->Elapsed;
        if (remaining <= 5.0f && remaining > 0.1f && !alertFired)
        {
            alertFired = true;
            Log.Information($"[XIVSchAssitant] [CooldownAlert] {label} revient dans {remaining:F1}s — declenchement compte a rebours.");
            TriggerLocalCountdown(remaining);
        }
    }

    // ── Dispose ───────────────────────────────────────────────────────────────

    public void Dispose()
    {
        _countdownFont?.Dispose();
        _sendPacketHook?.Dispose();
        PluginInterface.UiBuilder.Draw -= OnDraw;
        ClientState.Login            -= OnLogin;
        ClientState.ClassJobChanged  -= OnClassJobChanged;
        ClientState.TerritoryChanged -= OnTerritoryChanged;
        Framework.Update             -= OnFrameworkUpdate;
        DutyState.DutyWiped          -= OnDutyWiped;
        Log.Information("[XIVSchAssitant] Plugin decharge.");
    }
}
