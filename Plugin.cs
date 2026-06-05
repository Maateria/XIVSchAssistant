using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Command;
using Dalamud.Game.DutyState;
using Dalamud.Hooking;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Interface.Windowing;
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

    // Status effect "Biolysis" (Loi de l'infection) applique sur l'ennemi — ID 1895.
    private const uint BiolysisStatusId = 1895;

    // Spells pour l'alerte compte a rebours cooldown
    private const uint ChainStratagemActionId   = 7436;  // Stratagemes Entrelaces (~120s CD)

    // ── WinMM mciSendString (alerte sonore DoT avec controle volume) ─────────
    [DllImport("winmm.dll", CharSet = CharSet.Auto)]
    private static extern int mciSendString(string command,
        System.Text.StringBuilder? returnString, int returnLength, nint callback);

    // Seuil de distance (metres) en dessous duquel Eos est consideree "au centre"
    private const float PlacementThreshold = 3f;

    // Territoires pour lesquels l'alerte "centre invalide" a deja ete envoyee (session courante).
    private readonly HashSet<uint> _warnedDefaultCenterTerritories = new();

    // ── Hook SendPacket (injection coordonnees placement) ─────────────────────
    private unsafe delegate bool SendPacketDelegate(ZoneClient* self, nint packet, uint a3, uint a4, bool a5);
    private Hook<SendPacketDelegate>? _sendPacketHook;
    private bool    _interceptNextPlacePacket;
    private Vector3 _pendingPlaceCenter;

    // ── Verification post-placement ───────────────────────────────────────────
    // 2.5s apres un placement sans centre personnalise, on verifie qu'Eos a bien
    // atteint les coords cibles. Si elle est encore loin, les coords etaient invalides.
    private DateTime? _pendingCenterVerifyAt;
    private Vector3   _pendingCenterVerifyTarget;
    private uint      _pendingCenterVerifyTerritory;

    // ── Services Dalamud ──────────────────────────────────────────────────────
    [PluginService] internal IDalamudPluginInterface  PluginInterface { get; init; } = null!;
    [PluginService] internal IClientState             ClientState     { get; init; } = null!;
    [PluginService] internal ICondition               Condition       { get; init; } = null!;
    [PluginService] internal IFramework               Framework       { get; init; } = null!;
    [PluginService] internal IPluginLog               Log             { get; init; } = null!;
    [PluginService] internal IDataManager             Data            { get; init; } = null!;
    [PluginService] internal ITargetManager           TargetMgr       { get; init; } = null!;
    [PluginService] internal IBuddyList               BuddyList       { get; init; } = null!;
    [PluginService] internal IDutyState               DutyState       { get; init; } = null!;
    [PluginService] internal ICommandManager          CommandManager  { get; init; } = null!;
    [PluginService] internal IChatGui                 ChatGui         { get; init; } = null!;
    [PluginService] internal IGameInteropProvider     GameInterop     { get; init; } = null!;
    [PluginService] internal IGameGui                 GameGui         { get; init; } = null!;

    internal Configuration Config { get; }

    private readonly WindowSystem _windowSystem;
    private readonly ConfigWindow _configWindow;

    // ── Etat global ───────────────────────────────────────────────────────────
    // Vrai quand un DutyWiped vient d'etre detecte — en attente que le joueur
    // soit debout et hors chargement pour invoquer Eos.
    private bool      _pendingEosSummonAfterWipe;
    private DateTime? scheduledPlaceEos;
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

    // ── Noms localises des actions pet ───────────────────────────────────────
    // Charges depuis la feuille Excel "PetAction" dans la langue du client au demarrage.
    private string _placePetActionName = "Se placer"; // fallback FR

    // ── Fallback placement <me> pour boss dont la position est invalide ────────
    // 200ms apres la commande <t>, si le hook n'a pas ete declenche, on essaie <me>.
    private DateTime? _placeEosMeRetryAt;

    // ── Override manuel ───────────────────────────────────────────────────────
    // Vrai quand le joueur a place Eos manuellement — l'auto-recentrage est suspendu.
    // Se remet a false quand le joueur clique "Retour au centre", change de territoire,
    // ou qu'un wipe / swap de pet survient.
    private bool    _manualOverrideActive;
    private Vector2 _returnBtnPos  = new Vector2(-1f, -1f); // -1 = non initialise
    private bool    _btnIsDragging = false;

    // ── Detection changements d'entite ────────────────────────────────────────
    private uint _lastTargetId;      // EntityId de la derniere cible joueur
    private uint _lastEosPetEntityId; // EntityId du dernier pet detecte (Eos/Seraph)

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
        Config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Config.Initialize(PluginInterface);
        Log.Information("[XIVSchAssitant] Demarrage...");
        isInEightPlayerContent = IsEightPlayerContent(ClientState.TerritoryType);

        unsafe
        {
            var ps = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState.Instance();
            if (ps != null) currentJobId = ps->CurrentClassJobId;
        }

        _windowSystem = new WindowSystem("XIVSchAssitant");
        _configWindow = new ConfigWindow(Config, this);
        _windowSystem.AddWindow(_configWindow);
        PluginInterface.UiBuilder.Draw += _windowSystem.Draw;
        PluginInterface.UiBuilder.Draw += OnDraw;
        PluginInterface.UiBuilder.OpenConfigUi += OnOpenConfigUi;
        PluginInterface.UiBuilder.OpenMainUi   += OnOpenConfigUi;
        CommandManager.AddHandler("/xivsch", new CommandInfo(OnXivSchCommand)
        {
            HelpMessage = "Open XIVSchAssistant settings.",
        });
        // Police haute resolution pour le countdown — TrumpGothicE est le
        // font "grand format" du jeu (barres HP, titres), ideal pour des chiffres.
        _countdownFont = PluginInterface.UiBuilder.FontAtlas.NewGameFontHandle(
            new GameFontStyle(GameFontFamily.TrumpGothic, 96f));

        // Hook sur ZoneClient::SendPacket pour injecter les coordonnees du centre
        // dans le paquet de placement d'Eos avant qu'il parte vers le serveur.
        unsafe
        {
            _sendPacketHook = GameInterop.HookFromAddress<SendPacketDelegate>(
                (nint)ZoneClient.MemberFunctionPointers.SendPacket, OnSendPacket);
            _sendPacketHook.Enable();
        }

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

        Log.Information("[XIVSchAssitant] Charge.");
    }

    // ── Commandes et config UI ────────────────────────────────────────────────

    private void OnOpenConfigUi()
        => _configWindow.IsOpen = true;

    private void OnXivSchCommand(string command, string args)
        => _configWindow.IsOpen = true;

    internal void TestDotSound()
    {
        _lastDotAlertAt = DateTime.MinValue;
        PlayDotAlert();
    }

    internal void TestCountdown() => TriggerLocalCountdown(5f);

    // ── Evenements ────────────────────────────────────────────────────────────

    // Declenche quand tout le groupe est mort et que l'ecran passe au noir.
    // Ne fire PAS sur un rez individuel en combat → detection fiable du wipe global.
    private void OnDutyWiped(IDutyStateEventArgs args)
    {
        if (!Config.AutoSummonEnabled) return;
        if (currentJobId != ScholarJobId) return;
        Log.Information("[XIVSchAssitant] DutyWiped — Eos sera invoquee apres resurrection.");
        _pendingEosSummonAfterWipe = true;
        _manualOverrideActive = false; // apres wipe, retour au comportement auto
    }

    private void OnTerritoryChanged(uint territoryType)
    {
        _manualOverrideActive = false; // nouvel environnement → auto-recentrage actif
        isInEightPlayerContent = IsEightPlayerContent(territoryType);
        Log.Debug($"[XIVSchAssitant] Territoire {territoryType} — 8j : {isInEightPlayerContent}");
        // Delai plus long pour laisser le temps au cercle bleu de se resoudre
        // avant que le check periodique ne tente de repositionner Eos.
        if (isInEightPlayerContent) nextPeriodicCheck = DateTime.UtcNow.AddSeconds(15);

        // Verifier qu'Eos est invoquee dans tout contenu instancie (donjon, trial, raid...).
        // Delai de 5s pour laisser l'instance finir de charger avant de tenter l'invocation.
        if (Config.AutoSummonEnabled && IsAnyInstanceContent(territoryType))
        {
            Log.Debug($"[XIVSchAssitant] Entree instance — verification Eos dans 5s.");
            scheduledEosCheck = DateTime.UtcNow.AddSeconds(5);
        }
    }

    private void OnClassJobChanged(uint classJobId)
    {
        currentJobId = classJobId;
        if (!Config.AutoSummonEnabled) return;
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
        if (!ClientState.IsLoggedIn) return;

        // ── Post-wipe : attente resurrection ─────────────────────────────────────
        if (Config.AutoSummonEnabled && _pendingEosSummonAfterWipe
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

        // ── Placement en attente ──────────────────────────────────────────────
        // Execute ici (et non dans TryPlaceEos) pour rester sur le thread principal
        // Dalamud, hors de tout contexte de callback d'evenement.
        if (_pendingPlaceEos)
        {
            _pendingPlaceEos = false;
            DoPlaceEos();
        }

        // ── Fallback placement <me> (boss dont la position est invalide) ─────────
        // Si 200ms apres la commande <t> le hook n'a pas ete declenche, c'est que
        // la commande a ete rejetee (Cible invalide). On tente alors <me>.
        if (_placeEosMeRetryAt.HasValue && DateTime.UtcNow >= _placeEosMeRetryAt.Value)
        {
            _placeEosMeRetryAt = null;
            if (_interceptNextPlacePacket) // hook toujours arme → <t> a echoue
            {
                Log.Information("[XIVSchAssitant] [Place] <t> rejete — fallback <me>.");
                unsafe { TrySendPlaceCommandMe(); }
            }
        }

        // ── Verification post-placement ───────────────────────────────────────
        if (_pendingCenterVerifyAt.HasValue && DateTime.UtcNow >= _pendingCenterVerifyAt.Value)
        {
            var verifyTarget    = _pendingCenterVerifyTarget;
            var verifyTerritory = _pendingCenterVerifyTerritory;
            _pendingCenterVerifyAt = null;
            VerifyEosCenterReached(verifyTarget, verifyTerritory);
        }

        // ── Detection acquisition de cible ────────────────────────────────────
        {
            uint newTargetId = TargetMgr.Target != null ? TargetMgr.Target.EntityId : 0u;
            if (newTargetId != _lastTargetId)
            {
                bool wasNoTarget = _lastTargetId == 0;
                _lastTargetId = newTargetId;
                if (wasNoTarget && newTargetId != 0
                    && Config.AutoPlaceEnabled
                    && isInEightPlayerContent && currentJobId == ScholarJobId)
                {
                    Log.Information(
                        $"[XIVSchAssitant] [TargetChange] Target acquired (0x{newTargetId:X8}) — repositioning.");
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
        if (Config.AutoPlaceEnabled && isInEightPlayerContent && currentJobId == ScholarJobId)
            DetectPetEntityChange();

        // ── Verification periodique ───────────────────────────────────────────
        if (Config.AutoPlaceEnabled && isInEightPlayerContent && currentJobId == ScholarJobId
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

        // ── Alerte cooldown Chain Stratagem ──────────────────────────────────────
        if ((Config.CountdownOverlayEnabled || Config.CountdownSoundEnabled)
            && currentJobId == ScholarJobId)
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
                    if (Config.CountdownSoundEnabled)
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
        if (Config.AutoPlaceEnabled && isInEightPlayerContent)
        {
            scheduledPlaceEos = DateTime.UtcNow.AddSeconds(PlaceEosDelay);
            Log.Information($"[XIVSchAssitant] Placement planifie dans {PlaceEosDelay}s.");
        }
    }

    // Appele quand le joueur se connecte sur son personnage.
    // Necessaire pour invoquer Eos si le perso est deja en Erudit au login.
    private void OnLogin()
    {
        if (!Config.AutoSummonEnabled) return;
        Log.Information("[XIVSchAssitant] Connexion detectee — verification Eos dans 5s.");
        scheduledEosCheck = DateTime.UtcNow.AddSeconds(5);
    }

    // Verifie qu'Eos est invoquee et l'invoque si absente.
    // Appele par les chemins differee (login, entree instance).
    // Utilise BuddyList.PetBuddy comme PandorasBox/AutoFairy.
    private unsafe void CheckAndSummonEos()
    {
        if (!Config.AutoSummonEnabled) return;
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

    // Envoie "/petaction Se placer <t>" avec injection des coordonnees du centre dans
    // le paquet reseau resultant. Si <t> est rejete (Cible invalide — boss hors arene),
    // un fallback <me> est tente 200ms plus tard via OnFrameworkUpdate.
    // Appele depuis Framework.Update (_pendingPlaceEos).
    private unsafe void DoPlaceEos()
    {
        var territory = ClientState.TerritoryType;
        var center    = GetArenaCenter();

        // Planifier une verification 2.5s apres le placement (uniquement si pas de centre custom).
        if (!Config.CustomCenters.ContainsKey(territory))
        {
            _pendingCenterVerifyAt        = DateTime.UtcNow.AddSeconds(2.5);
            _pendingCenterVerifyTarget    = center;
            _pendingCenterVerifyTerritory = territory;
        }

        // Armer le hook AVANT d'envoyer la commande : OnSendPacket intercepte le paquet
        // opcode=0x0030 / actionId=3 dans les ~4ms et remplace X/Y/Z par le centre.
        _pendingPlaceCenter       = center;
        _interceptNextPlacePacket = true;
        _placeEosMeRetryAt        = null; // reset d'un eventuel timer precedent
        SendPlaceChatCommand();
    }

    // ── Commandes chat ────────────────────────────────────────────────────────

    // Envoie /petaction "Se placer" <t> via ProcessChatBoxEntry.
    // Si la cible est absente, annule et differe.
    // Si la cible est valide, arme le timer fallback <me> (200ms).
    private unsafe void SendPlaceChatCommand()
    {
        var uiModule = UIModule.Instance();
        if (uiModule == null) return;
        if (TargetMgr.Target == null)
        {
            _interceptNextPlacePacket = false;
            Log.Debug("[XIVSchAssitant] [ChatCmd] Pas de cible — placement differe.");
            return;
        }
        // Fallback <me> dans 200ms si le hook n'a pas ete declenche (→ <t> rejete).
        _placeEosMeRetryAt = DateTime.UtcNow.AddMilliseconds(200);
        Log.Information($"[XIVSchAssitant] [ChatCmd] {_placePetActionName} <t>");
        var msg = new FFXIVClientStructs.FFXIV.Client.System.String.Utf8String(
            $@"/petaction ""{_placePetActionName}"" <t>");
        uiModule->ProcessChatBoxEntry(&msg, 0, false);
    }

    // Fallback : tente /petaction "Se placer" <me> quand <t> a ete rejete.
    // Le hook reste arme — s'il intercepte le paquet <me>, il injecte le centre.
    // Si <me> est aussi rejete (necessiterait un ennemi ?), le hook reste arme
    // jusqu'au prochain cycle de repositionnement (qui le resettera).
    private unsafe void TrySendPlaceCommandMe()
    {
        var uiModule = UIModule.Instance();
        if (uiModule == null) { _interceptNextPlacePacket = false; return; }
        Log.Information($"[XIVSchAssitant] [ChatCmd] Fallback {_placePetActionName} <me>");
        var msg = new FFXIVClientStructs.FFXIV.Client.System.String.Utf8String(
            $@"/petaction ""{_placePetActionName}"" <me>");
        uiModule->ProcessChatBoxEntry(&msg, 0, false);
    }

    // ── Hook ZoneClient.SendPacket ────────────────────────────────────────────
    // Intercepte les paquets opcode=0x0030 / actionId=3 (Se placer).
    // Structure du paquet (commande pet, taille stockee a +0x00) :
    //   +0x00 : uint  taille totale du buffer
    //   +0x08 : ushort opcode
    //   +0x24 : uint  actionId (3 = Se placer)
    //   +0x34 : float X destination
    //   +0x38 : float Y destination
    //   +0x3C : float Z destination
    // NB : a5=True pour "Attendre" uniquement (Se placer a a5=False).
    private unsafe bool OnSendPacket(ZoneClient* self, nint packet, uint a3, uint a4, bool a5)
    {
        if (packet != nint.Zero)
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
                            if (_interceptNextPlacePacket)
                            {
                                // Placement initie par le plugin — injection des coords du centre.
                                float origX = *(float*)(packet + 0x34);
                                float origZ = *(float*)(packet + 0x3C);
                                *(float*)(packet + 0x34) = _pendingPlaceCenter.X;
                                *(float*)(packet + 0x38) = 0f;
                                *(float*)(packet + 0x3C) = _pendingPlaceCenter.Z;
                                _interceptNextPlacePacket = false;
                                _placeEosMeRetryAt        = null;
                                Log.Information(
                                    $"[XIVSchAssitant] [PacketInject] " +
                                    $"({origX:F2},_,{origZ:F2}) → " +
                                    $"({_pendingPlaceCenter.X:F2},0,{_pendingPlaceCenter.Z:F2})");
                            }
                            else if (isInEightPlayerContent && Config.AutoPlaceEnabled)
                            {
                                // Placement manuel du joueur — suspendre l'auto-recentrage.
                                _manualOverrideActive = true;
                                Log.Information("[XIVSchAssitant] [Manual] Placement manuel — auto-recentrage suspendu.");
                            }
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
            _manualOverrideActive = false; // nouveau pet → retour au comportement auto
            nextPeriodicCheck = DateTime.UtcNow;
        }
    }

    // ── Verification periodique et repositionnement ───────────────────────────

    private unsafe void CheckAndRepositionEos()
    {
        // Placement manuel actif → ne pas repositionner automatiquement.
        if (_manualOverrideActive)
        {
            nextPeriodicCheck = DateTime.UtcNow.AddSeconds(5.0);
            return;
        }

        var playerObj = GameObjectManager.Instance()->Objects.IndexSorted[0].Value;
        if (playerObj == null) return;
        var playerId = playerObj->EntityId;

        for (var i = 0; i < 200; i++)
        {
            var obj = GameObjectManager.Instance()->Objects.IndexSorted[i].Value;
            if (obj == null) continue;
            if (obj->OwnerId != playerId || obj->ObjectKind != ObjectKind.BattleNpc) continue;

            float cx = 100f, cz = 100f;
            if (Config.CustomCenters.TryGetValue(ClientState.TerritoryType, out var customCenter))
            { cx = customCenter.X; cz = customCenter.Z; }

            float dx   = obj->Position.X - cx;
            float dz   = obj->Position.Z - cz;
            float dist = MathF.Sqrt(dx * dx + dz * dz);

            Log.Debug(
                $"[XIVSchAssitant] Eos @ ({obj->Position.X:F1},{obj->Position.Z:F1}) dist={dist:F1}");

            if (dist <= PlacementThreshold)
            {
                bool inCombat = Condition[ConditionFlag.InCombat];
                nextPeriodicCheck = DateTime.UtcNow.AddSeconds(inCombat ? 2.0 : 5.0);
                return;
            }

            // Eos hors centre : repositionnement.
            // Intervalle fixe 5s entre tentatives — evite le spam de commandes.
            // (La detection d'acquisition de cible declenche un check immediat
            //  via nextPeriodicCheck = DateTime.UtcNow dans OnFrameworkUpdate.)
            Log.Debug($"[XIVSchAssitant] Eos hors centre (dist={dist:F1}) — repositionnement.");
            nextPeriodicCheck = DateTime.UtcNow.AddSeconds(5.0);
            TryPlaceEos();
            return;
        }

        // Pet non trouve (Dissipation en cours, etc.) — check rapide a 1s.
        _lastEosPetEntityId = 0;
        nextPeriodicCheck = DateTime.UtcNow.AddSeconds(1);
        Log.Debug("[XIVSchAssitant] Pet non trouve.");
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
        if (Config.CustomCenters.TryGetValue(ClientState.TerritoryType, out var custom))
        { x = custom.X; z = custom.Z; }

        var obj = GameObjectManager.Instance()->Objects.IndexSorted[0].Value;
        float y = obj != null ? obj->Position.Y : 0f;

        return new Vector3(x, y, z);
    }

    // ── Helpers pour ConfigWindow ─────────────────────────────────────────────

    internal uint CurrentTerritoryType => ClientState.TerritoryType;

    internal unsafe (float X, float Z)? GetPlayerFlatPosition()
    {
        var playerObj = GameObjectManager.Instance()->Objects.IndexSorted[0].Value;
        if (playerObj == null) return null;
        return (playerObj->Position.X, playerObj->Position.Z);
    }

    // Remet la position du bouton "Retour au centre" a la valeur par defaut (centre ecran).
    internal void ResetReturnBtnPos()
    {
        _returnBtnPos = new Vector2(-1f, -1f); // force reinitialisation au prochain Draw
    }

    internal unsafe (float X, float Z)? GetEosFlatPosition()
    {
        var playerObj = GameObjectManager.Instance()->Objects.IndexSorted[0].Value;
        if (playerObj == null) return null;
        uint playerId = playerObj->EntityId;
        for (var i = 1; i < 200; i++)
        {
            var o = GameObjectManager.Instance()->Objects.IndexSorted[i].Value;
            if (o == null) continue;
            if (o->OwnerId == playerId && o->ObjectKind == ObjectKind.BattleNpc)
                return (o->Position.X, o->Position.Z);
        }
        return null;
    }

    // Verifie 2.5s apres un placement que Eos est bien arrivee aux coords cibles.
    // Si elle est encore loin (>8m), les coordonnees etaient invalides pour cette arene
    // et on affiche une alerte rouge dans le chat pour que le joueur configure un centre custom.
    private unsafe void VerifyEosCenterReached(Vector3 expectedCenter, uint territory)
    {
        if (!Config.AutoPlaceEnabled) return;
        // Si un centre custom existe maintenant (configure entre le placement et la verif), on ne warn pas.
        if (Config.CustomCenters.ContainsKey(territory)) return;
        // Une seule alerte par territoire par session.
        if (!_warnedDefaultCenterTerritories.Add(territory)) return;

        var playerObj = GameObjectManager.Instance()->Objects.IndexSorted[0].Value;
        if (playerObj == null) return;
        uint playerId = playerObj->EntityId;

        for (var i = 1; i < 200; i++)
        {
            var obj = GameObjectManager.Instance()->Objects.IndexSorted[i].Value;
            if (obj == null) continue;
            if (obj->OwnerId != playerId || obj->ObjectKind != ObjectKind.BattleNpc) continue;

            float dx   = obj->Position.X - expectedCenter.X;
            float dz   = obj->Position.Z - expectedCenter.Z;
            float dist = MathF.Sqrt(dx * dx + dz * dz);

            if (dist > 8f)
            {
                Log.Warning(
                    $"[XIVSchAssistant] Center verification failed for territory {territory}: " +
                    $"Eos is {dist:F1}m from expected center ({expectedCenter.X:F1}, {expectedCenter.Z:F1}).");
                ChatGui.PrintError(
                    $"[XIVSchAssistant] ⚠ Arena center unreachable for territory {territory}! " +
                    "Eos could not reach the default position (100, 100) — " +
                    "this arena has a non-standard center. " +
                    "Use /xivsch → Custom Arena Centers to configure the correct position.");
            }
            return;
        }
    }

    // ── Noms localises PetAction ──────────────────────────────────────────────

    // Struct minimal compatible Lumina pour lire la feuille "PetAction" brute.
    // La feuille n'existe pas en tant que type genere dans Lumina.Excel.Sheets,
    // mais ExcelModule accepte tout type implementant IExcelRow<T>.
    // Structure de la feuille PetAction :
    //   col 0  Name    string (localise — "Se placer" / "Place" / "Platzieren" / "配置")
    [Sheet("PetAction")]
    private readonly struct PetActionRow(ExcelPage page, uint offset, uint row)
        : IExcelRow<PetActionRow>
    {
        public uint      RowId      => row;
        public ExcelPage ExcelPage  => page;
        public uint      RowOffset  => offset;
        // Colonne 0 : Name — chaine localisee du nom de l'action.
        public ReadOnlySeString Name => page.ReadString(offset, offset);
        static PetActionRow IExcelRow<PetActionRow>.Create(
            ExcelPage page, uint offset, uint row) => new(page, offset, row);
    }

    // Charge le nom localise "Se placer" depuis les donnees Excel du jeu dans la
    // langue du client. Fallback FR si le chargement echoue.
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
            Log.Information(
                $"[XIVSchAssitant] PetAction localise ({ClientState.ClientLanguage}) : " +
                $"place='{_placePetActionName}'");
        }
        catch (Exception ex)
        {
            Log.Warning(
                $"[XIVSchAssitant] PetAction lookup echoue : {ex.Message} " +
                "— fallback FR actif.");
        }
    }

    // ── Alerte sonore DoT ─────────────────────────────────────────────────────

    private void PlayDotAlert()
    {
        if ((DateTime.UtcNow - _lastDotAlertAt).TotalSeconds < 3.0) return;
        _lastDotAlertAt = DateTime.UtcNow;
        Log.Debug("[XIVSchAssitant] [DotAlert] Biolysis tombe — lecture son.");
        try
        {
            if (_dotSoundPath == null || !File.Exists(_dotSoundPath))
            { Log.Warning("[XIVSchAssitant] [DotAlert] Fichier son manquant."); return; }

            int vol = Math.Clamp((int)(Config.DotAlertVolume * 1000f), 0, 1000);
            mciSendString("close xivschsnd", null, 0, nint.Zero);
            mciSendString($"open \"{_dotSoundPath}\" alias xivschsnd", null, 0, nint.Zero);
            mciSendString($"setaudio xivschsnd volume to {vol}", null, 0, nint.Zero);
            mciSendString("play xivschsnd", null, 0, nint.Zero);
        }
        catch (Exception ex)
        {
            Log.Warning($"[XIVSchAssitant] [DotAlert] Erreur audio : {ex.Message}");
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
        _localCountdownLastTick  = (int)Math.Ceiling(seconds);
        if (Config.CountdownSoundEnabled)
            unsafe { UIGlobals.PlaySoundEffect(48); }
        Log.Information($"[XIVSchAssitant] [CooldownAlert] Compte a rebours {seconds:F1}s (overlay ImGui).");
    }

    // Rendu ImGui : grand chiffre jaune-or dessine directement sur la ForegroundDrawList.
    // Pas de fenetre ImGui — aucun artefact de frame/padding, centrage pixel-perfect.
    private void OnDraw()
    {
        // ── Bouton "Retour au centre" (override manuel actif) ─────────────────
        if (_manualOverrideActive && Config.AutoPlaceEnabled
            && isInEightPlayerContent && currentJobId == ScholarJobId
            && ClientState.IsLoggedIn)
            DrawReturnToCenterButton();

        if (!_localCountdownActive || !Config.CountdownOverlayEnabled) return;
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

    // Bouton draggable "Retour au centre" — meme taille que les boutons de la barre de
    // familier (~46x46 px). Apparait au centre de l'ecran (40% Y) la premiere fois.
    // Drag gere manuellement via InvisibleButton + io.MouseDelta : click gauche = action,
    // maintien + mouvement = deplacement. Position persistee en config.
    private void DrawReturnToCenterButton()
    {
        const float Btn = 46f;
        var io = ImGui.GetIO();

        // Initialisation de la position au premier appel.
        // Si la config contient une valeur dans le bas de l'ecran (< 85% Y), c'est un
        // reste de l'ancienne version — on ignore et on recentre.
        if (_returnBtnPos.X < 0f)
        {
            bool savedOk = !float.IsNaN(Config.ReturnBtnX)
                        && !float.IsNaN(Config.ReturnBtnY)
                        && Config.ReturnBtnY < io.DisplaySize.Y * 0.85f;
            _returnBtnPos = savedOk
                ? new Vector2(Config.ReturnBtnX, Config.ReturnBtnY)
                : new Vector2(io.DisplaySize.X * 0.5f - Btn * 0.5f,
                              io.DisplaySize.Y * 0.4f - Btn * 0.5f);
        }

        // Clamp a l'ecran
        _returnBtnPos = new Vector2(
            Math.Clamp(_returnBtnPos.X, 0f, io.DisplaySize.X - Btn),
            Math.Clamp(_returnBtnPos.Y, 0f, io.DisplaySize.Y - Btn));

        // Fenetre positionnee manuellement (NoMove) — le drag est gere par InvisibleButton.
        ImGui.SetNextWindowPos(_returnBtnPos, ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(Btn, Btn), ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0f);

        var wflags = ImGuiWindowFlags.NoTitleBar  | ImGuiWindowFlags.NoResize         |
                     ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
                     ImGuiWindowFlags.NoCollapse  | ImGuiWindowFlags.NoMove            |
                     ImGuiWindowFlags.NoNav       | ImGuiWindowFlags.NoFocusOnAppearing;

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding,   Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);

        if (ImGui.Begin("##EosCenterReturn", wflags))
        {
            var origin = ImGui.GetCursorScreenPos();
            var dl     = ImGui.GetWindowDrawList();

            // Zone d'interaction couvrant toute la surface (drag ET click).
            ImGui.InvisibleButton("##ibtn", new Vector2(Btn, Btn));
            bool hovered  = ImGui.IsItemHovered();
            bool active   = ImGui.IsItemActive();
            bool released = ImGui.IsItemDeactivated();

            // Drag : souris maintenue + deplacement > 3px
            if (active && ImGui.IsMouseDragging(ImGuiMouseButton.Left, 3f))
            {
                _btnIsDragging = true;
                _returnBtnPos += io.MouseDelta;
            }

            // Release : fin de drag (sauvegarde) ou clic simple (action)
            if (released)
            {
                if (_btnIsDragging)
                {
                    Config.ReturnBtnX = _returnBtnPos.X;
                    Config.ReturnBtnY = _returnBtnPos.Y;
                    Config.Save();
                    Log.Debug($"[XIVSchAssitant] [Btn] Pos sauvegardee ({_returnBtnPos.X:F0},{_returnBtnPos.Y:F0})");
                }
                else
                {
                    // Verifier qu'une cible est presente avant de declencher le placement.
                    // Sans cible, la commande pet sera differee indefiniment — on previent
                    // l'utilisateur et on garde le bouton visible.
                    if (TargetMgr.Target == null)
                    {
                        ChatGui.PrintError(
                            "[XIVSchAssistant] No target selected — please target the boss first, then click again to return Eos to the arena center.");
                        Log.Debug("[XIVSchAssitant] [Manual] Clic sans cible — bouton conserve.");
                    }
                    else
                    {
                        _manualOverrideActive = false;
                        nextPeriodicCheck     = DateTime.UtcNow;
                        Log.Information("[XIVSchAssitant] [Manual] Retour au centre demande.");
                    }
                }
                _btnIsDragging = false;
            }

            // Apparence : fond sombre type slot FFXIV, bordure bleue Scholar
            uint colBg = active
                ? ImGui.ColorConvertFloat4ToU32(new Vector4(0.04f, 0.22f, 0.58f, 1.00f))
                : hovered
                    ? ImGui.ColorConvertFloat4ToU32(new Vector4(0.15f, 0.40f, 0.78f, 0.97f))
                    : ImGui.ColorConvertFloat4ToU32(new Vector4(0.08f, 0.08f, 0.16f, 0.92f));
            uint colBd = ImGui.ColorConvertFloat4ToU32(new Vector4(0.50f, 0.72f, 1.00f, 0.85f));

            dl.AddRectFilled(origin, origin + new Vector2(Btn, Btn), colBg, 4f);
            dl.AddRect(origin, origin + new Vector2(Btn, Btn), colBd, 4f, 0, 1.5f);

            // Icone "cible de placement" : cercle + reticule + point central.
            // Symbolise clairement "placer Eos en ce point" (identique aux indicateurs
            // de ciblage au sol de FFXIV).
            var c  = origin + new Vector2(Btn * 0.5f, Btn * 0.5f); // centre du bouton
            float rOuter = Btn * 0.34f; // rayon du cercle exterieur
            float rDot   = Btn * 0.07f; // rayon du point central
            float gap    = Btn * 0.11f; // espace entre le point et les branches du reticule
            float alpha  = active ? 1.00f : hovered ? 0.95f : 0.88f;

            // Couleur principale : bleu clair Scholar
            uint colIcon = ImGui.ColorConvertFloat4ToU32(new Vector4(0.60f, 0.86f, 1.00f, alpha));
            // Accent interieur : blanc pour le point central
            uint colDot  = ImGui.ColorConvertFloat4ToU32(new Vector4(1.00f, 1.00f, 1.00f, alpha));

            // Cercle exterieur
            dl.AddCircle(c, rOuter, colIcon, 48, 1.8f);

            // 4 branches du reticule (du bord interne du gap jusqu'au cercle)
            float t1 = rDot + gap, t2 = rOuter - 1f;
            dl.AddLine(c + new Vector2(0f,  -t1), c + new Vector2(0f,  -t2), colIcon, 1.8f);
            dl.AddLine(c + new Vector2(0f,   t1), c + new Vector2(0f,   t2), colIcon, 1.8f);
            dl.AddLine(c + new Vector2(-t1, 0f),  c + new Vector2(-t2, 0f),  colIcon, 1.8f);
            dl.AddLine(c + new Vector2( t1, 0f),  c + new Vector2( t2, 0f),  colIcon, 1.8f);

            // Point central plein (blanc pour contraster)
            dl.AddCircleFilled(c, rDot, colDot);
            // Petit anneau autour du point pour la lisibilite
            dl.AddCircle(c, rDot + 2f, colIcon, 24, 1.2f);

            if (hovered)
                ImGui.SetTooltip("Return Eos to arena center\nDrag to reposition");
        }
        ImGui.End();
        ImGui.PopStyleVar(2);
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
        CommandManager.RemoveHandler("/xivsch");
        PluginInterface.UiBuilder.OpenConfigUi -= OnOpenConfigUi;
        PluginInterface.UiBuilder.OpenMainUi   -= OnOpenConfigUi;
        PluginInterface.UiBuilder.Draw -= _windowSystem.Draw;
        _windowSystem.RemoveAllWindows();
        mciSendString("close xivschsnd", null, 0, nint.Zero);
        _countdownFont?.Dispose();
        PluginInterface.UiBuilder.Draw -= OnDraw;
        ClientState.Login            -= OnLogin;
        ClientState.ClassJobChanged  -= OnClassJobChanged;
        ClientState.TerritoryChanged -= OnTerritoryChanged;
        Framework.Update             -= OnFrameworkUpdate;
        DutyState.DutyWiped          -= OnDutyWiped;
        _sendPacketHook?.Dispose();
        Log.Information("[XIVSchAssitant] Plugin decharge.");
    }
}
