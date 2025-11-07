using System;
using System.Linq;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Synthesis;

namespace leveledlistresolver
{
    sealed partial class RecordPatcher : IRecordPatcher<IContainerGetter, Container>
    {
        static readonly Container.TranslationMask ContMask = new(true)
        {
            FormVersion = false,
            VersionControl = false,
            Items = false,
        };

        static readonly Container.TranslationMask ContMask2 = new(false) { Flags = true };

        bool IRecordPatcher<IContainerGetter, Container>.Try(
            IPatcherState<ISkyrimMod, ISkyrimModGetter> state,
            FormKey formKey,
            out Container? setter
        )
        {
            setter = default;

            var extentContexts = state.LinkCache.GetExtentContexts<IContainerGetter>(formKey);
            if (extentContexts.Length < 2)
            {
                return false;
            }

            var highest = extentContexts[0].Record;
            var lowest = state.LinkCache.GetLowestOverride<IContainerGetter>(formKey);

            if (
                !Utility.HasConflict(
                    extentContexts,
                    lowest,
                    (r, l) => r.Equals(l, ContMask),
                    static r => r.Items
                )
            )
            {
                if (Program.Settings.VerboseLogging)
                    Console.WriteLine(
                        $"Skipped {highest.EditorID} [{formKey}] - no conflict detected\n"
                    );
                return false;
            }

            var copy = highest.DeepCopy();
            copy.FormVersion = 44;
            copy.VersionControl = Utility.Timestamp;
            copy.Items = [];

            bool hasEditorIdConflict = !string.Equals(
                lowest.EditorID,
                copy.EditorID,
                StringComparison.InvariantCulture
            );
            bool hasFlagsConflict = !copy.Equals(lowest, ContMask2);

            Utility.ResolveEditorIdConflict(extentContexts, lowest, copy, ref hasEditorIdConflict);

            Utility.ResolvePropertyConflicts(
                extentContexts,
                lowest,
                (l, r) => l.Equals(r, ContMask2),
                ref hasFlagsConflict,
                record => copy.Flags = record.Flags
            );

            var items = Utility.MergeEntries(extentContexts, lowest, static r => r.Items);

            copy.Items.AddRange(items.Select(static i => i.DeepCopy()));

            Utility.LogVerboseMergeInfo(
                state,
                extentContexts,
                formKey,
                copy.EditorID,
                i => i.Mod?.Containers.ContainsKey(formKey) ?? false
            );

            if (
                Utility.ShouldSkipUnchanged(
                    highest,
                    copy,
                    (c, h) => ((Container)c).Equals(h, ContMask),
                    static h => h.Items,
                    static c => ((Container)c).Items,
                    copy.EditorID
                )
            )
            {
                return false;
            }

            if (Program.Settings.VerboseLogging)
                Console.WriteLine();
            setter = copy;
            state.PatchMod.Containers.Set(copy);
            return true;
        }
    }
}
