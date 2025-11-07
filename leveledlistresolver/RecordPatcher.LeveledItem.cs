using System;
using System.Linq;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Synthesis;
using Noggog;

namespace leveledlistresolver
{
    sealed partial class RecordPatcher : IRecordPatcher<ILeveledItemGetter, LeveledItem>
    {
        static readonly LeveledItem.TranslationMask LvliMask = new(true)
        {
            FormVersion = false,
            VersionControl = false,
            Version2 = false,
            Entries = false,
        };

        static readonly LeveledItem.TranslationMask LvliMask2 = new(false)
        {
            ChanceNone = true,
            Flags = true,
            Global = true,
        };

        bool IRecordPatcher<ILeveledItemGetter, LeveledItem>.Try(
            IPatcherState<ISkyrimMod, ISkyrimModGetter> state,
            FormKey formKey,
            out LeveledItem? setter
        )
        {
            setter = default;

            var extentContexts = state.LinkCache.GetExtentContexts<ILeveledItemGetter>(formKey);
            if (extentContexts.Length < 2)
            {
                if (extentContexts.Length == 0)
                    return false;

                var winning = extentContexts[0].Record;
                if (winning.Entries?.Any(static i => i.IsNullEntry()) ?? false)
                {
                    setter = state.PatchMod.LeveledItems.GetOrAddAsOverride(winning);
                    if (Program.Settings.VerboseLogging)
                        Console.WriteLine(
                            $"Removed {setter.Entries!.RemoveAll(Utility.IsNullEntry)} null entries from {setter.EditorID} [{formKey}]{Environment.NewLine}"
                        );
                    else
                        setter.Entries!.RemoveAll(Utility.IsNullEntry);
                    return true;
                }

                return false;
            }

            var highest = extentContexts[0].Record;
            var lowest = state.LinkCache.GetLowestOverride<ILeveledItemGetter>(formKey);

            if (
                !Utility.HasConflict(
                    extentContexts,
                    lowest,
                    (r, l) => r.Equals(l, LvliMask),
                    static r => r.Entries
                )
            )
            {
                if (Program.Settings.VerboseLogging)
                    Console.WriteLine(
                        $"Skipped {highest.EditorID} [{formKey}] - no conflict detected\n"
                    );
                return false;
            }

            var copy = state.PatchMod.LeveledItems.GetOrAddAsOverride(highest);
            copy.FormVersion = 44;
            copy.VersionControl = Utility.Timestamp;
            copy.Entries = [];

            bool hasEditorIdConflict = !string.Equals(
                lowest.EditorID,
                copy.EditorID,
                StringComparison.InvariantCulture
            );
            bool hasPropertiesConflict = !copy.Equals(lowest, LvliMask2);

            Utility.ResolveEditorIdConflict(extentContexts, lowest, copy, ref hasEditorIdConflict);

            Utility.ResolvePropertyConflicts(
                extentContexts,
                lowest,
                (l, r) => l.Equals(r, LvliMask2),
                ref hasPropertiesConflict,
                record =>
                {
                    copy.ChanceNone = record.ChanceNone;
                    copy.Flags = record.Flags;
                    copy.Global.SetTo(record.Global);
                }
            );

            var entries = Utility.MergeEntries(extentContexts, lowest, static r => r.Entries);

            Utility.ProcessLeveledListEntries(
                entries,
                state.LinkCache,
                static e => e.IsNullEntry(),
                static (e, lc) => e.IsNullOrEmptySublist(lc),
                static e => e.Data?.Level ?? 0
            );

            if (entries.Count > 255)
            {
                int i = 1;
                foreach (var chunk in entries.Chunk(255))
                {
                    LeveledItem item = new(state.PatchMod, $"{copy.EditorID}Chunk_{i}")
                    {
                        FormVersion = 44,
                        VersionControl = Utility.Timestamp,
                        ChanceNone = copy.ChanceNone,
                        Flags = copy.Flags,
                        Global = copy.Global.AsNullable(),
                        Entries = chunk.Select(static i => i.DeepCopy()).ToExtendedList(),
                    };

                    state.PatchMod.LeveledItems.Add(item);

                    LeveledItemEntry entry = new()
                    {
                        Data = new()
                        {
                            Level = 1,
                            Reference = item.ToLink(),
                            Count = 1,
                        },
                    };

                    copy.Entries.Add(entry);
                    i++;
                }
            }
            else
            {
                copy.Entries.AddRange(entries.Select(static i => i.DeepCopy()));
            }

            Utility.LogVerboseMergeInfo(
                state,
                extentContexts,
                formKey,
                copy.EditorID,
                i => i.Mod?.LeveledItems.ContainsKey(formKey) ?? false
            );

            if (
                Utility.ShouldSkipUnchanged(
                    highest,
                    copy,
                    (c, h) => ((LeveledItem)c).Equals(h, LvliMask),
                    static h => h.Entries,
                    static c => ((LeveledItem)c).Entries,
                    copy.EditorID
                )
            )
            {
                return false;
            }

            if (Program.Settings.VerboseLogging)
                Console.WriteLine();
            setter = copy;
            return true;
        }
    }
}
