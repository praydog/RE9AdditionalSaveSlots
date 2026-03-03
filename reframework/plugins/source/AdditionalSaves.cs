// RE9 Additional Save Slots — C# REFramework plugin
// Increases the number of Game save slots from 12 to 90 (configurable).
//
// RE9 uses a partition-based save system (app.SaveServiceManager).
// Default layout:
//   System    : slot -1       (1 slot)
//   Auto      : slots 0~1    (2 slots)
//   Game      : slots 10~21  (12 slots)  <-- expanded by this plugin
//   UDGame_0  : slot 100     (1 slot)
//   UDGame_1  : slot 200     (1 slot)
//   UDGame_2  : slot 300     (1 slot)
//   UDGame_3  : slots 400~420 (21 slots)
//
// Max safe value is 90 (range 10-99) to avoid overlapping with UDGame_0 at slot 100.

using System;
using REFrameworkNET;
using REFrameworkNET.Attributes;
using REFrameworkNET.Callbacks;

public class AdditionalSavesPlugin {
    // ── Configuration ──────────────────────────────────────────────
    const int MAX_GAME_SAVES = 90;

    // ── State ──────────────────────────────────────────────────────
    static bool initialized;
    static app.GuiSaveLoadController.Unit pendingUnit;

    // ================================================================
    //  Entry / Exit
    // ================================================================
    [PluginEntryPoint]
    public static void Main() {
        API.LogInfo("[AdditionalSaves] C# plugin loaded. Waiting for SaveServiceManager...");
    }

    [PluginExitPoint]
    public static void OnUnload() {
        initialized = false;
        pendingUnit = null;
        API.LogInfo("[AdditionalSaves] C# plugin unloaded.");
    }

    // ================================================================
    //  UpdateBehavior — poll until SaveServiceManager is initialized
    // ================================================================
    [Callback(typeof(UpdateBehavior), CallbackType.Pre)]
    public static void OnUpdateBehavior() {
        if (initialized) return;

        var saveMgr = API.GetManagedSingletonT<app.SaveServiceManager>();
        if (saveMgr == null) return;

        if (!saveMgr.IsInitialized) return;

        if (ExpandGamePartition(saveMgr)) {
            initialized = true;
            API.LogInfo($"[AdditionalSaves] Initialization complete. MAX_GAME_SAVES = {MAX_GAME_SAVES}");
        }
    }

    // ================================================================
    //  Hook 1: makeSaveDataList post — expand array if too small
    // ================================================================
    [MethodHook(typeof(app.GuiSaveLoadModel), nameof(app.GuiSaveLoadModel.makeSaveDataList), MethodHookType.Post)]
    public static void OnMakeSaveDataListPost(ref ulong retval) {
        if (!initialized) return;

        var arrMo = ManagedObject.ToManagedObject(retval);
        if (arrMo == null) return;

        var arr = arrMo.As<_System.Array>();
        if (arr == null) return;

        int len = arr.Length;
        if (len >= MAX_GAME_SAVES) return;

        // Create expanded array
        var newArrMo = app.GuiSaveDataInfo.REFType.CreateManagedArray((uint)MAX_GAME_SAVES);
        if (newArrMo == null) {
            API.LogWarning("[AdditionalSaves] Failed to create expanded array");
            return;
        }
        newArrMo.Globalize();
        var newArr = newArrMo.As<_System.Array>();

        // Copy existing elements
        for (int i = 0; i < len; i++) {
            var elem = arr.GetValue(i);
            if (elem != null)
                newArr.SetValue(elem, i);
        }

        // Fill remaining slots via makeSaveData (instance method called with null —
        // it doesn't touch 'this', so this works despite being an instance method)
        var makeSaveData = app.GuiSaveLoadModel.REFType
            .GetMethod("makeSaveData(app.SaveSlotCategory, System.Int32)");

        if (makeSaveData != null) {
            for (int i = len; i < MAX_GAME_SAVES; i++) {
                try {
                    object info = null;
                    makeSaveData.HandleInvokeMember_Internal(null,
                        new object[] { (int)app.SaveSlotCategory.Game, i }, ref info);
                    if (info is ManagedObject infoMo) {
                        infoMo.Globalize();
                        newArr.SetValue(infoMo, i);
                    }
                } catch { }
            }
        }

        API.LogInfo($"[AdditionalSaves] Expanded makeSaveDataList: {len} -> {MAX_GAME_SAVES}");
        retval = newArrMo.GetAddress();
    }

    // ================================================================
    //  Hook 2: onSetup pre — capture the unit reference
    // ================================================================
    [MethodHook(typeof(app.GuiSaveLoadController.Unit), nameof(app.GuiSaveLoadController.Unit.onSetup), MethodHookType.Pre)]
    public static PreHookResult OnSetupPre(Span<ulong> args) {
        pendingUnit = ManagedObject.ToManagedObject(args[1])?.As<app.GuiSaveLoadController.Unit>();
        return PreHookResult.Continue;
    }

    // ================================================================
    //  Hook 2: onSetup post — patch _SaveItemNum
    // ================================================================
    [MethodHook(typeof(app.GuiSaveLoadController.Unit), nameof(app.GuiSaveLoadController.Unit.onSetup), MethodHookType.Post)]
    public static void OnSetupPost(ref ulong retval) {
        if (!initialized || pendingUnit == null) return;

        try {
            int current = pendingUnit._SaveItemNum;
            if (current < MAX_GAME_SAVES) {
                pendingUnit._SaveItemNum = MAX_GAME_SAVES;
                API.LogInfo($"[AdditionalSaves] Patched GUI _SaveItemNum: {current} -> {MAX_GAME_SAVES}");
            }
        } catch (Exception e) {
            API.LogWarning($"[AdditionalSaves] onSetup patch failed: {e.Message}");
        }

        pendingUnit = null;
    }

    // ================================================================
    //  Core: expand the Game partition's slot count
    // ================================================================
    static bool ExpandGamePartition(app.SaveServiceManager saveMgr) {
        var itemSet = GetDefaultSegmentItemSet(saveMgr);
        if (itemSet == null) return false;

        // toValueArray() returns ManagedSaveSlotPartition[]
        var partitionsArrMo = (itemSet as IObject)?.Call("toValueArray()") as ManagedObject;
        if (partitionsArrMo == null) {
            API.LogInfo("[AdditionalSaves] Could not get partitions array");
            return false;
        }

        var partitionsArr = partitionsArrMo.As<_System.Array>();
        int arrSize = partitionsArr.Length;
        API.LogInfo($"[AdditionalSaves] Found {arrSize} partitions in Default_0 segment");

        // Find the Game partition
        app.SaveSlotPartition gamePartition = null;
        int gamePartitionSlots = 0;

        for (int i = 0; i < arrSize; i++) {
            var partMo = partitionsArr.GetValue(i) as ManagedObject;
            if (partMo == null) continue;

            var part = partMo.As<app.SaveSlotPartition>();
            if (part == null) continue;

            API.LogInfo($"[AdditionalSaves]   Partition {i}: usage={(int)part._Usage} headSlotId={part._HeadSlotId} slotCount={part._SlotCount}");

            if (part._Usage == app.SaveSlotCategory.Game) {
                gamePartition = part;
                gamePartitionSlots = part._SlotCount;
            }
        }

        if (gamePartition == null) {
            API.LogInfo("[AdditionalSaves] Could not find Game partition (category=Game)");
            return false;
        }

        if (gamePartitionSlots >= MAX_GAME_SAVES) {
            API.LogInfo($"[AdditionalSaves] Game partition already has {gamePartitionSlots} slots, nothing to do");
            return true;
        }

        int extraSlots = MAX_GAME_SAVES - gamePartitionSlots;

        // Patch _SlotCount on the partition
        gamePartition._SlotCount = MAX_GAME_SAVES;
        API.LogInfo($"[AdditionalSaves] Patched Game partition _SlotCount: {gamePartitionSlots} -> {MAX_GAME_SAVES}");

        // Patch _MaxUseSaveSlotCount on the manager
        int oldMax = saveMgr._MaxUseSaveSlotCount;
        int newMax = oldMax + extraSlots;
        saveMgr._MaxUseSaveSlotCount = newMax;
        API.LogInfo($"[AdditionalSaves] Patched _MaxUseSaveSlotCount: {oldMax} -> {newMax}");

        // Reload save slot info from disk so higher slot IDs are discovered
        try { saveMgr.reloadSaveSlotInfo(); }
        catch (Exception e) { API.LogWarning($"[AdditionalSaves] reloadSaveSlotInfo failed: {e.Message}"); }

        return true;
    }

    // ================================================================
    //  Navigate to partition item set
    // ================================================================
    static ManagedObject GetDefaultSegmentItemSet(app.SaveServiceManager saveMgr) {
        // _SaveSlotPartitions is a generic CatalogSetDictionary — navigate via IObject
        var partitionsDict = (saveMgr as IObject).GetField("_SaveSlotPartitions") as ManagedObject;
        if (partitionsDict == null) {
            API.LogInfo("[AdditionalSaves] Could not access _SaveSlotPartitions");
            return null;
        }

        // Try getValue(Default_0) -> _Source
        ManagedObject valueColl = null;
        try {
            valueColl = (partitionsDict as IObject)?.Call(
                "getValue(app.SaveSlotSegmentType)", (int)app.SaveSlotSegmentType.Default_0) as ManagedObject;
        } catch { }

        if (valueColl != null) {
            var itemSet = valueColl.GetField("_Source") as ManagedObject;
            if (itemSet != null) return itemSet;
            API.LogInfo("[AdditionalSaves] Could not read _Source from ValueCollection");
        }

        // Fallback: _Dict -> FindValue
        API.LogInfo("[AdditionalSaves] getValue(Default_0) failed, trying _Dict fallback");
        var dict = partitionsDict.GetField("_Dict") as ManagedObject;
        if (dict == null) {
            API.LogInfo("[AdditionalSaves] Could not access _Dict");
            return null;
        }

        return (dict as IObject)?.Call(
            "FindValue(app.SaveSlotSegmentType)", (int)app.SaveSlotSegmentType.Default_0) as ManagedObject;
    }
}
