using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using Lumina.Excel.Sheets;

namespace XIVSchAssitant;

public sealed class Plugin : IDalamudPlugin
{
    private const uint ScholarJobId      = 28;
    private const uint SummonEosActionId = 17215;
    private const double PlaceEosDelay   = 3.0;

    private const uint       PlacePetActionId   = 3;
    private const ActionType PlacePetActionType = (ActionType)11;

    private static readonly Dictionary<uint, (float X, float Z)> ArenaExceptions = new();

    // ── Hook UseActionLocation ────────────────────────────────────────────────
    private unsafe delegate bool UseActionLocationDelegate(
        ActionManager* self, ActionType actionType, uint actionId,
        ulong targetObjectId, Vector3* location, uint param, byte a7);
    private Hook<UseActionLocationDelegate>? _ualHook;

    // ── Hook UseAction ────────────────────────────────────────────────────────
    private unsafe delegate bool UseActionDelegate(
        ActionManager* self, ActionType actionType, uint actionId,
        ulong targetId, uint param, uint mode, uint comboRouteId, bool* outOptAreaTargeted);
    private Hook<UseActionDelegate>? _useActionHook;

    // ── Services ──────────────────────────────────────────────────────────────
    [PluginService] internal IDalamudPluginInterface PluginInterface { get; init; } = null!;
    [PluginService] internal IClientState  ClientState  { get; init; } = null!;
    [PluginService] internal ICondition    Condition    { get; init; } = null!;
    [PluginService] internal IFramework    Framework    { get; init; } = null!;
    [PluginService] internal IPluginLog    Log          { get; init; } = null!;
    [PluginService] internal IDataManager  Data         { get; init; } = null!;
    [PluginService] internal IGameInteropProvider GameInterop { get; init; } = null!;

    internal Configuration Config { get; }

    // ── Etat global ───────────────────────────────────────────────────────────
    private bool      wasUnconscious;
    private DateTime? scheduledResurrectionSummon;
    private DateTime? scheduledPlaceEos;
    private uint      currentJobId;
    private bool      isInEightPlayerContent;
    private DateTime  nextPeriodicCheck = DateTime.MinValue;
    private const float PlacementThreshold = 3f;

    // ── Etat redirect Eos ─────────────────────────────────────────────────────
    // Phase 1 : appel UAL direct depuis Framework.Update (hors hook, contexte propre).
    // Phase 2 : si UAL echoue, ecriture memoire directe sur les offsets destination.
    private bool      _pendingDirectUAL;    // tenter UAL au prochain tick
    private DateTime? _pendingMemWriteAt;   // moment de l'ecriture memoire (fallback)
    private uint      _eosOwner;            // EntityId du joueur (pour retrouver Eos)
    private int       _memWriteRetry;       // nb de tentatives restantes (evite boucle infinie)

    public Plugin()
    {
        Log.Information("[XIVSchAssitant] Demarrage...");
        Config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        isInEightPlayerContent = IsEightPlayerContent(ClientState.TerritoryType);

        unsafe
        {
            var ps = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState.Instance();
            if (ps != null) currentJobId = ps->CurrentClassJobId;

            _ualHook = GameInterop.HookFromAddress<UseActionLocationDelegate>(
                (nint)ActionManager.MemberFunctionPointers.UseActionLocation,
                OnUseActionLocation);
            _ualHook.Enable();

            _useActionHook = GameInterop.HookFromAddress<UseActionDelegate>(
                (nint)ActionManager.MemberFunctionPointers.UseAction,
                OnUseAction);
            _useActionHook.Enable();
        }

        ClientState.ClassJobChanged  += OnClassJobChanged;
        ClientState.TerritoryChanged += OnTerritoryChanged;
        Framework.Update             += OnFrameworkUpdate;

        Log.Information("[XIVSchAssitant] Charge — hooks UAL + UA actifs.");
    }

    // ── Hook UseActionLocation (log seulement) ────────────────────────────────

    private unsafe bool OnUseActionLocation(
        ActionManager* self, ActionType actionType, uint actionId,
        ulong targetObjectId, Vector3* location, uint param, byte a7)
    {
        if ((byte)actionType == 11 && actionId == PlacePetActionId)
        {
            var pos = location != null ? *location : Vector3.Zero;
            Log.Information(
                $"[XIVSchAssitant] [UAL] type=11 id=3 " +
                $"target=0x{targetObjectId:X8} pos=({pos.X:F2},{pos.Y:F2},{pos.Z:F2}) " +
                $"param={param} a7={a7}");
        }
        return _ualHook!.Original(self, actionType, actionId, targetObjectId, location, param, a7);
    }

    // ── Hook UseAction (log seulement) ────────────────────────────────────────

    private unsafe bool OnUseAction(
        ActionManager* self, ActionType actionType, uint actionId,
        ulong targetId, uint param, uint mode, uint comboRouteId, bool* outOptAreaTargeted)
    {
        if ((byte)actionType == 11 && actionId == PlacePetActionId)
        {
            Log.Information(
                $"[XIVSchAssitant] [UA] type=11 id=3 target=0x{targetId:X8} mode={mode}");
        }
        return _useActionHook!.Original(
            self, actionType, actionId, targetId, param, mode, comboRouteId, outOptAreaTargeted);
    }

    // ── FindEosObject ─────────────────────────────────────────────────────────

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

    // ── Helpers ───────────────────────────────────────────────────────────────

    private bool IsEightPlayerContent(uint territoryType)
    {
        if (territoryType == 0) return false;
        try
        {
            var sheet = Data.GetExcelSheet<ContentFinderCondition>();
            if (sheet == null) return false;
            foreach (var row in sheet)
            {
                try { if (row.TerritoryType.RowId == territoryType) return row.ContentType.RowId is 4 or 5; }
                catch { }
            }
        }
        catch (Exception ex) { Log.Warning($"[XIVSchAssitant] IsEightPlayerContent: {ex.Message}"); }
        return false;
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

        // ── Placement planifie ────────────────────────────────────────────────
        if (scheduledPlaceEos.HasValue && DateTime.UtcNow >= scheduledPlaceEos.Value)
        {
            scheduledPlaceEos = null;
            TryPlaceEos();
        }

        // ── Phase 1 : UAL direct (contexte propre, hors hook) ─────────────────
        if (_pendingDirectUAL)
        {
            _pendingDirectUAL = false;
            DoDirectUAL();
        }

        // ── Phase 2 : ecriture memoire destination Eos ────────────────────────
        if (_pendingMemWriteAt.HasValue && DateTime.UtcNow >= _pendingMemWriteAt.Value)
        {
            _pendingMemWriteAt = null;
            DoMemWrite();
        }

        // ── Verification periodique du centre ─────────────────────────────────
        if (isInEightPlayerContent && currentJobId == ScholarJobId
            && DateTime.UtcNow >= nextPeriodicCheck)
        {
            nextPeriodicCheck = DateTime.UtcNow.AddSeconds(5);
            CheckAndRepositionEos();
        }
    }

    // ── Invocation ────────────────────────────────────────────────────────────

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

    // ── Placement ─────────────────────────────────────────────────────────────

    private unsafe void TryPlaceEos()
    {
        var playerObj = GameObjectManager.Instance()->Objects.IndexSorted[0].Value;
        if (playerObj == null) return;

        var eosObj = FindEosObject(playerObj->EntityId);
        if (eosObj == null)
        {
            Log.Warning("[XIVSchAssitant] TryPlaceEos — Eos non trouvee.");
            return;
        }

        Log.Information(
            $"[XIVSchAssitant] TryPlaceEos — joueur=({playerObj->Position.X:F1},{playerObj->Position.Z:F1}) " +
            $"Eos=({eosObj->Position.X:F2},{eosObj->Position.Z:F2})");

        _eosOwner      = playerObj->EntityId;
        _memWriteRetry = 3;
        // Tenter UAL direct au prochain tick Framework.Update (contexte propre)
        _pendingDirectUAL = true;
    }

    // ── Phase 1 : UAL direct depuis Framework.Update ──────────────────────────
    // Appel UseActionLocation hors de tout hook, sur le thread principal du jeu.
    // Teste si le jeu accepte un placement directionnel sans passer par le curseur.

    private unsafe void DoDirectUAL()
    {
        var am        = ActionManager.Instance();
        var playerObj = GameObjectManager.Instance()->Objects.IndexSorted[0].Value;
        if (am == null || playerObj == null)
        {
            Log.Warning("[XIVSchAssitant] [DirectUAL] am ou joueur null — abandon.");
            return;
        }

        var center = GetArenaCenter();
        Log.Information(
            $"[XIVSchAssitant] [DirectUAL] centre=({center.X:F1},{center.Y:F1},{center.Z:F1}) " +
            $"target=0x{playerObj->EntityId:X8}");

        // Appel via Original pour bypasser notre hook (evite log parasite).
        // Si le jeu envoie le paquet "deplace Eos vers center" → succes, on s'arrete ici.
        bool result = _ualHook!.Original(
            am, PlacePetActionType, PlacePetActionId,
            playerObj->EntityId, &center, 0, 0);

        Log.Information($"[XIVSchAssitant] [DirectUAL] result={result}");

        if (result)
        {
            // UAL acceptee : Eos devrait aller au centre. Rien d'autre a faire.
            Log.Information("[XIVSchAssitant] [DirectUAL] Succes — Eos en route vers le centre.");
            return;
        }

        // UAL refusee : passage en phase 2.
        // On envoie d'abord la commande (Eos part vers le joueur),
        // puis on ecrase la destination en memoire une fois que le serveur l'a ecrite (~500ms).
        Log.Information("[XIVSchAssitant] [DirectUAL] Echec — fallback ecriture memoire.");
        SendPlaceChatCommand();
        _pendingMemWriteAt = DateTime.UtcNow.AddMilliseconds(700);
    }

    private static unsafe void SendPlaceChatCommand()
    {
        var uiModule = FFXIVClientStructs.FFXIV.Client.UI.UIModule.Instance();
        if (uiModule == null) return;
        var msg = new FFXIVClientStructs.FFXIV.Client.System.String.Utf8String(
            @"/petaction ""Se placer"" <me>");
        uiModule->ProcessChatBoxEntry(&msg, 0, false);
    }

    // ── Phase 2 : ecriture memoire directe sur les offsets destination d'Eos ──
    // Offsets confirmes par scan :
    //   +0x3B0 = destination X  (float)
    //   +0x3B8 = destination Z  (float)
    //   +0x4E0 = vitesse (0=idle, 6=en deplacement)
    // Le serveur ecrit ces champs env. 500ms apres l'envoi de la commande.
    // On ecrase a t+700ms pour rediriger Eos vers (100, Y, 100) au lieu du joueur.

    private unsafe void DoMemWrite()
    {
        var eosObj = FindEosObject(_eosOwner);
        if (eosObj == null)
        {
            Log.Warning("[XIVSchAssitant] [MemWrite] Eos non trouvee.");
            return;
        }

        nint  eosBase = (nint)eosObj;
        float destX   = *(float*)(eosBase + 0x3B0);
        float destZ   = *(float*)(eosBase + 0x3B8);
        float speed   = *(float*)(eosBase + 0x4E0);

        Log.Information(
            $"[XIVSchAssitant] [MemWrite] Avant : dest=({destX:F2},{destZ:F2}) speed={speed:F2}");

        if (speed < 0.1f)
        {
            // Eos pas encore en mouvement — le paquet serveur n'est pas arrive.
            // Reessayer dans 300ms (jusqu'a _memWriteRetry fois).
            if (_memWriteRetry > 0)
            {
                _memWriteRetry--;
                Log.Warning($"[XIVSchAssitant] [MemWrite] Eos immobile — retry dans 300ms ({_memWriteRetry} restant(s)).");
                _pendingMemWriteAt = DateTime.UtcNow.AddMilliseconds(300);
            }
            else
            {
                Log.Warning("[XIVSchAssitant] [MemWrite] Eos toujours immobile apres retries — abandon.");
            }
            return;
        }

        // Eos est en mouvement : ecraser la destination.
        var center = GetArenaCenter();
        *(float*)(eosBase + 0x3B0) = center.X;
        *(float*)(eosBase + 0x3B8) = center.Z;

        // Verification immediate
        float verX = *(float*)(eosBase + 0x3B0);
        float verZ = *(float*)(eosBase + 0x3B8);
        Log.Information(
            $"[XIVSchAssitant] [MemWrite] Apres : dest=({verX:F2},{verZ:F2})  cible=({center.X:F1},{center.Z:F1})");
    }

    // ── Verification periodique ───────────────────────────────────────────────

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
                $"[XIVSchAssitant] Eos @ ({obj->Position.X:F1},{obj->Position.Y:F1},{obj->Position.Z:F1}) " +
                $"dist={dist:F1}");

            if (dist <= PlacementThreshold) return;

            Log.Debug("[XIVSchAssitant] Eos hors du centre — repositionnement.");
            TryPlaceEos();
            return;
        }

        Log.Debug("[XIVSchAssitant] Eos non trouvee.");
    }

    // ── Centre de l'arene ─────────────────────────────────────────────────────

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
        _ualHook?.Dispose();
        _useActionHook?.Dispose();
        ClientState.ClassJobChanged  -= OnClassJobChanged;
        ClientState.TerritoryChanged -= OnTerritoryChanged;
        Framework.Update             -= OnFrameworkUpdate;
        Log.Information("[XIVSchAssitant] Plugin decharge.");
    }
}
