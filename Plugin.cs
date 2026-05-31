using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Application.Network;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using Lumina.Excel.Sheets;

namespace XIVSchAssitant;

public sealed class Plugin : IDalamudPlugin
{
    private const uint   ScholarJobId      = 28;
    private const uint   SummonEosActionId = 17215;
    private const double PlaceEosDelay     = 3.0;

    // Action "Se placer" : type=11 (pet action), id=3
    private const uint PlacePetActionId = 3;

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

    internal Configuration Config { get; }

    // ── Etat global ───────────────────────────────────────────────────────────
    private bool      wasUnconscious;
    private DateTime? scheduledResurrectionSummon;
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

        ClientState.Login            += OnLogin;
        ClientState.ClassJobChanged  += OnClassJobChanged;
        ClientState.TerritoryChanged += OnTerritoryChanged;
        Framework.Update             += OnFrameworkUpdate;

        Log.Information("[XIVSchAssitant] Charge — hook ZoneClient.SendPacket actif.");
    }

    // ── Evenements ────────────────────────────────────────────────────────────

    private void OnTerritoryChanged(uint territoryType)
    {
        isInEightPlayerContent = IsEightPlayerContent(territoryType);
        Log.Debug($"[XIVSchAssitant] Territoire {territoryType} — 8j : {isInEightPlayerContent}");
        if (isInEightPlayerContent) nextPeriodicCheck = DateTime.UtcNow.AddSeconds(5);

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

        // ── Resurrection ──────────────────────────────────────────────────────
        bool isUnconscious = Condition[ConditionFlag.Unconscious];
        if (wasUnconscious && !isUnconscious && currentJobId == ScholarJobId)
        {
            Log.Information("[XIVSchAssitant] Resurrection — invocation d'Eos programmee.");
            scheduledResurrectionSummon = DateTime.UtcNow.AddSeconds(Config.ResurrectionDelaySeconds);
        }
        wasUnconscious = isUnconscious;

        if (scheduledResurrectionSummon.HasValue && DateTime.UtcNow >= scheduledResurrectionSummon.Value)
        {
            scheduledResurrectionSummon = null;
            if (currentJobId == ScholarJobId) TrySummonEos();
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
            Log.Information("[XIVSchAssitant] [ChatCmd] Se placer <t>");
            var msg = new FFXIVClientStructs.FFXIV.Client.System.String.Utf8String(
                @"/petaction ""Se placer"" <t>");
            uiModule->ProcessChatBoxEntry(&msg, 0, false);
        }
        else
        {
            Log.Information("[XIVSchAssitant] [ChatCmd] Se placer <me>");
            var msg = new FFXIVClientStructs.FFXIV.Client.System.String.Utf8String(
                @"/petaction ""Se placer"" <me>");
            uiModule->ProcessChatBoxEntry(&msg, 0, false);
        }
    }

    private unsafe void SendStayCommand()
    {
        var uiModule = FFXIVClientStructs.FFXIV.Client.UI.UIModule.Instance();
        if (uiModule == null) return;
        Log.Information("[XIVSchAssitant] [ChatCmd] Attendre <me>");
        var msg = new FFXIVClientStructs.FFXIV.Client.System.String.Utf8String(
            @"/petaction ""Attendre"" <me>");
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
            // Intervalle court (1s) si pas de cible, pour reagir vite a l'acquisition du boss.
            double interval = (_lastTargetId == 0) ? 1.0 : 5.0;
            Log.Debug($"[XIVSchAssitant] Eos hors centre (dist={dist:F1}) — repositionnement.");
            nextPeriodicCheck = DateTime.UtcNow.AddSeconds(interval);
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
        if (ArenaExceptions.TryGetValue(ClientState.TerritoryType, out var ex))
        { x = ex.X; z = ex.Z; }

        var obj = GameObjectManager.Instance()->Objects.IndexSorted[0].Value;
        float y = obj != null ? obj->Position.Y : 0f;

        return new Vector3(x, y, z);
    }

    // ── Dispose ───────────────────────────────────────────────────────────────

    public void Dispose()
    {
        _sendPacketHook?.Dispose();
        ClientState.Login            -= OnLogin;
        ClientState.ClassJobChanged  -= OnClassJobChanged;
        ClientState.TerritoryChanged -= OnTerritoryChanged;
        Framework.Update             -= OnFrameworkUpdate;
        Log.Information("[XIVSchAssitant] Plugin decharge.");
    }
}
