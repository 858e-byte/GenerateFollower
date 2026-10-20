using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace GenerateFollower;

/// <summary>Construit le mod outil GF_Exporter : une quête (startquest GF_ExporterQuest) + la liste des presets à exporter.</summary>
public static class ExporterBuilder
{
    public static void Run(Context ctx)
    {
        var rows = PresetTable.Read(ctx.CsvPath).Where(r => r.Inclure).ToList();
        if (rows.Count == 0) throw new GfException("Aucun preset avec inclure=oui dans presets.csv.");

        var list = new Dictionary<string, List<string>> { ["id"] = [], ["preset"] = [], ["raceplugin"] = [] };
        var ints = new Dictionary<string, List<int>> { ["raceid"] = [], ["female"] = [] };
        foreach (var r in rows)
        {
            var race = ctx.Need<IRaceGetter>(r.Race, $"race du preset {r.Preset}");
            list["id"].Add(r.Id);
            list["preset"].Add(r.Preset);
            list["raceplugin"].Add(race.FormKey.ModKey.FileName);
            ints["raceid"].Add((int)race.FormKey.ID);
            ints["female"].Add(r.Female ? 1 : 0);
        }

        var pex = Packaging.CompilePapyrus(ctx);
        var modDir = Packaging.CleanModDir(ctx, "GF_Exporter");

        var mod = new SkyrimMod(ModKey.FromFileName(ctx.Config.ExporterPluginName), SkyrimRelease.SkyrimSE);
        mod.ModHeader.Flags |= SkyrimModHeader.HeaderFlag.Small;
        mod.ModHeader.Author = "GenerateFollower";
        mod.ModHeader.Description = "Outil : exporte les têtes des presets RaceMenu. À désactiver après usage.";
        var quest = mod.Quests.AddNew("GF_ExporterQuest");
        quest.Name = "GenerateFollower - export des têtes";
        quest.Priority = 10;
        quest.VirtualMachineAdapter = new QuestAdapter { Scripts = { new ScriptEntry { Name = "GF_ExporterQuest" } } };
        mod.BeginWrite.ToPath(Path.Combine(modDir, ctx.Config.ExporterPluginName)).WithNoLoadOrder().Write();

        Packaging.CopyPex(pex, modDir, "GF_ExporterQuest");
        Packaging.WriteJsonUtil(Path.Combine(modDir, "SKSE", "Plugins", "StorageUtilData", "GenerateFollower", "export_list.json"), list, ints);
        var zip = Packaging.Zip(ctx, modDir, "GF_Exporter");
        Console.WriteLine($"{rows.Count} presets à exporter -> {zip}");
    }
}
