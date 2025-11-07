using System;
using System.Linq;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Synthesis;
using Noggog;

namespace leveledlistresolver
{
    sealed partial class RecordPatcher : IRecordPatcher<ILeveledNpcGetter, LeveledNpc>
    {
        static readonly LeveledNpc.TranslationMask LvlnMask = new(true)
        {
            FormVersion = false,
            VersionControl = false,
            Version2 = false,
            Entries = false,
        };

        static readonly LeveledNpc.TranslationMask LvlnMask2 = new(false)
        {
            ChanceNone = true,
            Flags = true,
            Global = true,
        };

        bool IRecordPatcher<ILeveledNpcGetter, LeveledNpc>.Try(
            IPatcherState<ISkyrimMod, ISkyrimModGetter> state,
            FormKey formKey,
            out LeveledNpc? setter
        )
        {
            setter = default;

            var extentContexts = state.LinkCache.GetExtentContexts<ILeveledNpcGetter>(formKey);
            if (extentContexts.Length < 2)
            {
                if (extentContexts.Length == 0)
                    return false;

                var winning = extentContexts[0].Record;
                if (winning.Entries?.Any(static i => i.IsNullEntry()) ?? false)
                {
                    setter = state.PatchMod.LeveledNpcs.GetOrAddAsOverride(winning);
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
            var lowest = state.LinkCache.GetLowestOverride<ILeveledNpcGetter>(formKey);

            if (
                !Utility.HasConflict(
                    extentContexts,
                    lowest,
                    (r, l) => r.Equals(l, LvlnMask),
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

            var copy = state.PatchMod.LeveledNpcs.GetOrAddAsOverride(highest);
            copy.FormVersion = 44;
            copy.VersionControl = Utility.Timestamp;
            copy.Entries = [];

            bool hasEditorIdConflict = !string.Equals(
                lowest.EditorID,
                copy.EditorID,
                StringComparison.InvariantCulture
            );
            bool hasPropertiesConflict = !copy.Equals(lowest, LvlnMask2);

            Utility.ResolveEditorIdConflict(extentContexts, lowest, copy, ref hasEditorIdConflict);

            Utility.ResolvePropertyConflicts(
                extentContexts,
                lowest,
                (l, r) => l.Equals(r, LvlnMask2),
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
                    LeveledNpc lvln = new(state.PatchMod, $"{copy.EditorID}Chunk_{i}")
                    {
                        FormVersion = 44,
                        VersionControl = Utility.Timestamp,
                        ChanceNone = copy.ChanceNone,
                        Flags = copy.Flags,
                        Global = copy.Global.AsNullable(),
                        Entries = chunk.Select(static i => i.DeepCopy()).ToExtendedList(),
                    };

                    state.PatchMod.LeveledNpcs.Add(lvln);

                    LeveledNpcEntry entry = new()
                    {
                        Data = new()
                        {
                            Level = 1,
                            Reference = lvln.ToLink(),
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
                i => i.Mod?.LeveledNpcs.ContainsKey(formKey) ?? false
            );

            if (
                Utility.ShouldSkipUnchanged(
                    highest,
                    copy,
                    (c, h) => ((LeveledNpc)c).Equals(h, LvlnMask),
                    static h => h.Entries,
                    static c => ((LeveledNpc)c).Entries,
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
