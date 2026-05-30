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

    internal Configuration Config { get; }

    // ── Etat global ───────────────────────────────────────────────────────────
    private bool      wasUnconscious;
    private DateTime? scheduledResurrectionSummon;
    private DateTime? scheduledPlaceEos;
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
    }

    private void OnClassJobChanged(uint classJobId)
    {
        currentJobId = classJobId;
        if (!Config.Enabled) return;
        if (classJobId == ScholarJobId)
        {
            Log.Information("[XIVSchAssitant] Swap vers Erudit — invocation d'Eos.");
            TrySummonEos();
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

    // ── Placement d'Eos ───────────────────────────────────────────────────────

    private unsafe void TryPlaceEos()
    {
        var playerObj = GameObjectManager.Instance()->Objects.IndexSorted[0].Value;
        if (playerObj == null) return;

        if (FindEosObject(playerObj->EntityId) == null)
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
        ClientState.ClassJobChanged  -= OnClassJobChanged;
        ClientState.TerritoryChanged -= OnTerritoryChanged;
        Framework.Update             -= OnFrameworkUpdate;
        Log.Information("[XIVSchAssitant] Plugin decharge.");
    }
}
