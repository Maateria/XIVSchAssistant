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

    // ── Etat redirect Eos (Phase 1 : UAL direct ; Phase 2 : ecriture memoire) ─
    private bool      _pendingDirectUAL;
    private DateTime? _pendingMemWriteAt;
    private uint      _eosOwner;
    private int       _memWriteRetry;

    // ── Redirect continu (Phase 3) ────────────────────────────────────────────
    // Apres Dissipation, le serveur repousse Eos vers le joueur en continu
    // (mode Heel). On ecrase la destination CHAQUE FRAME pendant jusqu'a
    // ContinuousRedirectDuration secondes. Notre callback Framework.Update
    // s'execute APRES les updates internes du jeu → notre ecriture est
    // toujours la derniere du frame → Eos fait des progres net vers le centre.
    private bool     _continuousRedirect;
    private DateTime _continuousRedirectExpiry;
    private const double ContinuousRedirectDuration = 10.0; // secondes max
    // Mis a true quand "En attente" a ete envoye pour annuler le mode Heel.
    // Dans ce cas, le prochain MemWrite passe outre le check speed==0.
    private bool     _heelModeCancelPending;
    // Dernier envoi de "En attente" — evite le spam (cooldown 25s).
    private DateTime _lastStaySentAt = DateTime.MinValue;

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

        // Rechercher l'action "Se placer" dans toutes les sheets Excel
        SearchPetPlaceAction();

        Log.Information("[XIVSchAssitant] Charge — hooks UAL + UA actifs.");
    }

    // ── Hook UseActionLocation ────────────────────────────────────────────────

    private unsafe bool OnUseActionLocation(
        ActionManager* self, ActionType actionType, uint actionId,
        ulong targetObjectId, Vector3* location, uint param, byte a7)
    {
        // Logger TOUS les appels type=11 pour diagnostiquer les actions de pet
        if ((byte)actionType == 11)
        {
            var pos = location != null ? *location : Vector3.Zero;
            Log.Information(
                $"[XIVSchAssitant] [UAL] type=11 id={actionId} " +
                $"target=0x{targetObjectId:X8} pos=({pos.X:F2},{pos.Y:F2},{pos.Z:F2})");
        }
        return _ualHook!.Original(self, actionType, actionId, targetObjectId, location, param, a7);
    }

    // ── Hook UseAction ────────────────────────────────────────────────────────

    private unsafe bool OnUseAction(
        ActionManager* self, ActionType actionType, uint actionId,
        ulong targetId, uint param, uint mode, uint comboRouteId, bool* outOptAreaTargeted)
    {
        // Logger TOUS les appels type=11 pour identifier les IDs Garde/Talons etc.
        if ((byte)actionType == 11)
        {
            Log.Information(
                $"[XIVSchAssitant] [UA] type=11 id={actionId} target=0x{targetId:X8} mode={mode}");
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

    // ── Recherche action de placement au demarrage ───────────────────────────
    // Log tous les ActionType/ActionId potentiels pour "Se placer" sol.

    private void SearchPetPlaceAction()
    {
        try
        {
            // Chercher dans la sheet Action (actions regulieres)
            var actionSheet = Data.GetExcelSheet<Lumina.Excel.Sheets.Action>();
            if (actionSheet != null)
            {
                foreach (var row in actionSheet)
                {
                    try
                    {
                        string name = row.Name.ExtractText();
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        if (name.Contains("placer", StringComparison.OrdinalIgnoreCase)
                            || name.Contains("place", StringComparison.OrdinalIgnoreCase)
                            || name.Contains("Heel", StringComparison.OrdinalIgnoreCase)
                            || name.Contains("Guard", StringComparison.OrdinalIgnoreCase)
                            || name.Contains("Garde", StringComparison.OrdinalIgnoreCase)
                            || name.Contains("Talon", StringComparison.OrdinalIgnoreCase))
                        {
                            Log.Information(
                                $"[XIVSchAssitant] [ActionSheet] id={row.RowId} " +
                                $"name='{name}' cat={row.ActionCategory.RowId} " +
                                $"job={row.ClassJob.RowId} targetArea={row.TargetArea}");
                        }
                    }
                    catch { }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning($"[XIVSchAssitant] SearchPetPlaceAction: {ex.Message}");
        }
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

        // ── Phase 1 : UAL direct ──────────────────────────────────────────────
        if (_pendingDirectUAL)
        {
            _pendingDirectUAL = false;
            DoDirectUAL();
        }

        // ── Phase 2 : ecriture memoire one-shot (apres delai server) ──────────
        if (_pendingMemWriteAt.HasValue && DateTime.UtcNow >= _pendingMemWriteAt.Value)
        {
            _pendingMemWriteAt = null;
            DoMemWrite();
        }

        // ── Phase 3 : redirect continu chaque frame ───────────────────────────
        // Actif apres Dissipation : le serveur (mode Heel) ecrase notre
        // destination periodiquement. En reecrivant a chaque frame, on
        // s'assure que la DERNIERE valeur du frame est toujours (100, Y, 100).
        if (_continuousRedirect)
        {
            DoContinuousRedirect();
        }

        // ── Verification periodique du centre ─────────────────────────────────
        // Inhibe pendant le redirect continu pour ne pas envoyer une nouvelle
        // commande "Se placer <me>" qui remettrait Eos au joueur.
        if (!_continuousRedirect
            && isInEightPlayerContent && currentJobId == ScholarJobId
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
        // Si un redirect continu est deja actif et Eos est en mouvement vers
        // le centre, ne pas emettre une nouvelle commande (evite la boucle).
        if (_continuousRedirect)
        {
            Log.Debug("[XIVSchAssitant] TryPlaceEos ignoree — redirect continu actif.");
            return;
        }

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

        _eosOwner             = playerObj->EntityId;
        _memWriteRetry        = 3;
        _heelModeCancelPending = false;
        _pendingDirectUAL     = true;
    }

    // ── Phase 1 : UAL direct depuis Framework.Update ──────────────────────────

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
        // Cible 0xE0000000 = aucune entite / placement au sol.
        // L'action "Se placer" via clic au sol utilise ce targetObjectId,
        // contrairement a la version entite (<me>) qui retourne toujours False.
        const ulong GroundTarget = 0xE0000000UL;
        Log.Information(
            $"[XIVSchAssitant] [DirectUAL] centre=({center.X:F1},{center.Y:F1},{center.Z:F1}) " +
            $"target=0x{GroundTarget:X8} (sol)");

        bool result = _ualHook!.Original(
            am, PlacePetActionType, PlacePetActionId,
            GroundTarget, &center, 0, 0);

        Log.Information($"[XIVSchAssitant] [DirectUAL] result={result}");

        if (result)
        {
            Log.Information("[XIVSchAssitant] [DirectUAL] Succes — Eos en route vers le centre.");
            // Activer quand meme le redirect continu pour robustesse (mode Heel).
            StartContinuousRedirect();
            return;
        }

        // UAL refusee — fallback : commande chat + ecriture memoire one-shot + redirect continu.
        Log.Information("[XIVSchAssitant] [DirectUAL] Echec — fallback chat + memoire.");
        SendPlaceChatCommand();
        _pendingMemWriteAt = DateTime.UtcNow.AddMilliseconds(700);
        StartContinuousRedirect();
    }

    private void StartContinuousRedirect()
    {
        _continuousRedirect        = true;
        _continuousRedirectExpiry  = DateTime.UtcNow.AddSeconds(ContinuousRedirectDuration);
        Log.Information($"[XIVSchAssitant] [ContRedir] Demarre pour {ContinuousRedirectDuration}s.");
    }

    private unsafe void SendPlaceChatCommand()
    {
        var uiModule = FFXIVClientStructs.FFXIV.Client.UI.UIModule.Instance();
        if (uiModule == null) return;
        // Utiliser <t> (cible courante = boss) plutot que <me>.
        // "Se placer <me>" est interprete par le serveur comme "aller sur le joueur",
        // ce qui maintient le mode Heel. "<t>" sur un boss force un deplacement
        // vers une entite non-joueur, ce qui annule le Heel mode cote serveur.
        Log.Information("[XIVSchAssitant] [ChatCmd] Se placer <t>");
        var msg = new FFXIVClientStructs.FFXIV.Client.System.String.Utf8String(
            @"/petaction ""Se placer"" <t>");
        uiModule->ProcessChatBoxEntry(&msg, 0, false);
    }

    private unsafe void SendStayCommand()
    {
        var uiModule = FFXIVClientStructs.FFXIV.Client.UI.UIModule.Instance();
        if (uiModule == null) return;
        // "En attente" = Stay : desactive le mode Heel cote serveur.
        // Envoye quand MemWrite detecte que la vitesse reste 0 (Eos colle au joueur
        // via Heel). Si le nom francais est incorrect, la commande echoue
        // silencieusement (pas de crash).
        Log.Information("[XIVSchAssitant] [ChatCmd] Attendre (cancel Heel / verrouillage centre)");
        var msg = new FFXIVClientStructs.FFXIV.Client.System.String.Utf8String(
            @"/petaction ""Attendre""");
        uiModule->ProcessChatBoxEntry(&msg, 0, false);
    }

    // ── Phase 2 : ecriture memoire one-shot ──────────────────────────────────

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

        if (speed < 0.1f && !_heelModeCancelPending)
        {
            // Eos pas encore en mouvement (paquet serveur non arrive, ou dist~0).
            if (_memWriteRetry > 0)
            {
                _memWriteRetry--;
                Log.Warning(
                    $"[XIVSchAssitant] [MemWrite] Eos immobile — retry dans 300ms " +
                    $"({_memWriteRetry} restant(s)).");
                _pendingMemWriteAt = DateTime.UtcNow.AddMilliseconds(300);
            }
            else
            {
                // Eos toujours immobile apres retries (mode Heel actif, Eos deja au joueur).
                // Envoyer "En attente" pour annuler le Heel cote serveur, puis reessayer
                // une fois (en ignorant le check speed=0).
                Log.Warning(
                    "[XIVSchAssitant] [MemWrite] Eos immobile apres retries — " +
                    "envoi Attendre pour annuler mode Heel.");
                ScanHeelTargetField(eosBase);
                SendStayCommand();
                _heelModeCancelPending = true;
                _pendingMemWriteAt = DateTime.UtcNow.AddMilliseconds(600);
            }
            return;
        }
        if (speed < 0.1f && _heelModeCancelPending)
        {
            // "En attente" a ete envoye. Eos est immobile (mode Heel annule, Stay actif).
            // Forcer l'ecriture de la destination meme a vitesse 0 : le moteur de navigation
            // lira +0x3B0/3B8 = centre et mettra Eos en mouvement.
            _heelModeCancelPending = false;
            Log.Information("[XIVSchAssitant] [MemWrite] Passe forcee post-Stay (speed=0 ignore).");
            // fall-through vers l'ecriture ci-dessous
        }

        var center = GetArenaCenter();
        // Ecriture destination navigation uniquement.
        // NOTE : +0x0B0/0x0B8 = position entite logique (nameplate CS) — NE PAS
        // ecrire ici : deplace le nameplate sans bouger le skin/la source de soin.
        *(float*)(eosBase + 0x3B0) = center.X;
        *(float*)(eosBase + 0x3B8) = center.Z;

        float verX = *(float*)(eosBase + 0x3B0);
        float verZ = *(float*)(eosBase + 0x3B8);
        Log.Information(
            $"[XIVSchAssitant] [MemWrite] Apres : dest=({verX:F2},{verZ:F2})  cible=({center.X:F1},{center.Z:F1})");
    }

    // ── Scan diagnostique mode Heel ───────────────────────────────────────────
    // Cherche l'entity ID du joueur dans la memoire d'Eos pour identifier
    // le champ "cible Heel". Appele une seule fois quand retries epuises.

    private bool _heelScanDone;

    private unsafe void ScanHeelTargetField(nint eosBase)
    {
        if (_heelScanDone) return; // une seule fois par session
        _heelScanDone = true;

        var playerObj = GameObjectManager.Instance()->Objects.IndexSorted[0].Value;
        if (playerObj == null) return;

        uint playerIdLow  = playerObj->EntityId;
        ulong playerIdFull = playerObj->EntityId; // même valeur en 64 bits

        Log.Information(
            $"[XIVSchAssitant] [HeelScan] Debut — EntityId joueur = 0x{playerIdLow:X8}");

        // Scanner les offsets +0x00 a +0x600 par pas de 4 octets.
        // On cherche la valeur de l'entity ID du joueur (uint ou uint basse de ulong).
        var hits = new System.Text.StringBuilder();
        for (var off = 0; off <= 0x600; off += 4)
        {
            try
            {
                uint val32 = *(uint*)(eosBase + off);
                if (val32 == playerIdLow && playerIdLow != 0)
                    hits.Append($"+0x{off:X3}(u32) ");

                // Aussi tester uint interpretee comme float : player position ~100
                float vf = *(float*)(eosBase + off);
                // Les positions du joueur sont autour de 80-120 typiquement
                if (vf >= 75f && vf <= 125f && off != 0x3B0 && off != 0x3B8)
                    hits.Append($"+0x{off:X3}(f{vf:F1}) ");
            }
            catch { }
        }
        Log.Information($"[XIVSchAssitant] [HeelScan] EntityId 0x{playerIdLow:X8} trouve a : {hits}");

        // Log aussi des offsets suspects autour de 0x3B0 en brut
        Log.Information("[XIVSchAssitant] [HeelScan] Dump brut +0x380..+0x3D0 :");
        var dump = new System.Text.StringBuilder();
        for (var off = 0x380; off <= 0x3D0; off += 4)
        {
            uint v = *(uint*)(eosBase + off);
            float f = *(float*)(eosBase + off);
            dump.Append($"[+{off:X3}] 0x{v:X8}={f:F2}  ");
            if ((off - 0x380) % 0x10 == 0x0C) { Log.Information("[XIVSchAssitant] [HeelScan] " + dump); dump.Clear(); }
        }
        if (dump.Length > 0) Log.Information("[XIVSchAssitant] [HeelScan] " + dump);

        // Et autour de 0x4D0
        Log.Information("[XIVSchAssitant] [HeelScan] Dump brut +0x4D0..+0x500 :");
        dump.Clear();
        for (var off = 0x4C0; off <= 0x500; off += 4)
        {
            uint v = *(uint*)(eosBase + off);
            float f = *(float*)(eosBase + off);
            dump.Append($"[+{off:X3}] 0x{v:X8}={f:F2}  ");
            if ((off - 0x4C0) % 0x10 == 0x0C) { Log.Information("[XIVSchAssitant] [HeelScan] " + dump); dump.Clear(); }
        }
        if (dump.Length > 0) Log.Information("[XIVSchAssitant] [HeelScan] " + dump);
    }

    // ── Phase 3 : redirect continu chaque frame ───────────────────────────────
    // Cadence : ~60 fps. En mode Heel, le Heel AI (qui tourne APRES
    // Framework.Update) ecrase notre destination chaque frame.
    // En mode non-Heel (normal), notre ecriture est definitive.
    // Cette phase sert de secours au cas ou le Heel AI est ralenti ou coupe.
    //
    // IMPORTANT : on ne stoppe PAS sur la position — faux positifs possible.
    // On laisse tourner pendant ContinuousRedirectDuration secondes.
    // C'est CheckAndRepositionEos (apres expiry) qui reprend la main ensuite.

    private unsafe void DoContinuousRedirect()
    {
        // Timeout : on laisse la verif periodique reprendre
        if (DateTime.UtcNow >= _continuousRedirectExpiry)
        {
            _continuousRedirect = false;
            Log.Information("[XIVSchAssitant] [ContRedir] Expiry — redirection terminee.");

            // Si Eos est au centre a l'expiry, envoyer "En attente" pour verrouiller
            // sa position : annule la destination boss issue de "Se placer <t>".
            // Sans ca, Eos repart vers le boss des que ContRedir s'arrete.
            var eosAtExpiry = FindEosObject(_eosOwner);
            if (eosAtExpiry != null)
            {
                float targetCx = 100f, targetCz = 100f;
                if (ArenaExceptions.TryGetValue(ClientState.TerritoryType, out var exEntry))
                { targetCx = exEntry.X; targetCz = exEntry.Z; }

                // Utiliser la position CS (obj->Position) coherente avec CheckAndRepositionEos.
                float csXExp   = eosAtExpiry->Position.X;
                float csZExp   = eosAtExpiry->Position.Z;
                float distExp  = MathF.Sqrt(
                    (csXExp - targetCx) * (csXExp - targetCx) +
                    (csZExp - targetCz) * (csZExp - targetCz));
                Log.Information(
                    $"[XIVSchAssitant] [ContRedir] Expiry check: csPos=({csXExp:F2},{csZExp:F2}) " +
                    $"dist={distExp:F2} threshold={PlacementThreshold}");
                if (distExp < PlacementThreshold)
                {
                    Log.Information(
                        $"[XIVSchAssitant] [ContRedir] Eos au centre (dist={distExp:F1}) " +
                        "— En attente pour verrouiller.");
                    SendStayCommand();
                    _lastStaySentAt = DateTime.UtcNow;
                    // Ne pas relancer la verif immediatement : Eos est deja au centre,
                    // on repart au cycle normal de 5s.
                    nextPeriodicCheck = DateTime.UtcNow.AddSeconds(5);
                    return;
                }
            }
            else
            {
                Log.Warning("[XIVSchAssitant] [ContRedir] Expiry check: Eos non trouvee.");
            }

            nextPeriodicCheck = DateTime.UtcNow; // relancer la verif periodique immediatement
            return;
        }

        var eosObj = FindEosObject(_eosOwner);
        if (eosObj == null)
        {
            // Eos disparue (mort, renvoyee) — arreter
            _continuousRedirect = false;
            return;
        }

        float cx = 100f, cz = 100f;
        if (ArenaExceptions.TryGetValue(ClientState.TerritoryType, out var ex))
        { cx = ex.X; cz = ex.Z; }

        nint eosBase = (nint)eosObj;

        // Ecraser la destination de navigation vers le centre.
        // En mode non-Heel, cela suffit : Eos suit la destination.
        // En mode Heel (apres Dissipation), le Heel AI ecrase +0x3B0/3B8
        // apres notre write — mais chaque frame ou on gagne la course on
        // progresse legerement. Associe a la commande <t> (cible boss) dans
        // DoDirectUAL, cette approche finit par l'emporter.
        *(float*)(eosBase + 0x3B0) = cx;
        *(float*)(eosBase + 0x3B8) = cz;
        // NOTE : +0x0B0/0x0B8 = position entite logique (nameplate CS) — NE PAS
        // ecrire ici : ca deplace le nameplate sans deplacer le skin / la source
        // de soins, ce qui cree un bug visuel et fonctionnel.
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

            if (dist <= PlacementThreshold)
            {
                // Eos est au centre. Envoyer "En attente" periodiquement pour la
                // verrouiller sur place et annuler la destination boss issue de
                // "Se placer <t>". Cooldown 25s pour eviter le spam.
                if (!_continuousRedirect &&
                    (DateTime.UtcNow - _lastStaySentAt).TotalSeconds > 25.0)
                {
                    Log.Information("[XIVSchAssitant] [Periodic] Eos au centre — En attente (verrou).");
                    SendStayCommand();
                    _lastStaySentAt = DateTime.UtcNow;
                }
                return;
            }

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
