Scriptname GF_ExporterQuest extends Quest
{GenerateFollower : applique chaque preset de la liste sur le joueur puis exporte sa tete (CharGen.ExportHead).
Lancer en console : startquest GF_ExporterQuest. Les tetes deja exportees sont sautees (reprise possible).}

string Property ListFile = "GenerateFollower/export_list" AutoReadOnly
string Property LogFile = "Data/SKSE/Plugins/CharGen/GF_export_log.txt" AutoReadOnly

bool busy = false

Event OnInit()
	If IsRunning()
		RegisterForSingleUpdate(3.0)
	EndIf
EndEvent

Event OnUpdate()
	If busy
		return
	EndIf
	busy = true
	RunExport()
	busy = false
	Stop()
EndEvent

Function RunExport()
	Actor player = Game.GetPlayer()
	int n = JsonUtil.StringListCount(ListFile, "id")
	If n <= 0
		Debug.MessageBox("GenerateFollower : liste d'export vide ou introuvable (SKSE/Plugins/StorageUtilData/GenerateFollower/export_list.json).")
		return
	EndIf
	MiscUtil.WriteToFile(LogFile, "=== Export de " + n + " presets ===\n", true, true)
	Debug.Notification("GenerateFollower : export de " + n + " tetes, ne touche a rien.")

	int done = 0
	int skipped = 0
	int failed = 0
	int i = 0
	While i < n
		string id = JsonUtil.StringListGet(ListFile, "id", i)
		string preset = JsonUtil.StringListGet(ListFile, "preset", i)
		string outName = "GF_" + id
		If MiscUtil.FileExists("Data/SKSE/Plugins/CharGen/" + outName + ".nif")
			skipped += 1
		Else
			Race r = Game.GetFormFromFile(JsonUtil.IntListGet(ListFile, "raceid", i), JsonUtil.StringListGet(ListFile, "raceplugin", i)) as Race
			int female = JsonUtil.IntListGet(ListFile, "female", i)
			If ExportOne(player, r, female, preset, outName)
				done += 1
				MiscUtil.WriteToFile(LogFile, "OK     " + id + "  " + preset + "\n", true, false)
			Else
				failed += 1
				MiscUtil.WriteToFile(LogFile, "ECHEC  " + id + "  " + preset + "\n", true, false)
			EndIf
			If (done + failed) % 5 == 0
				Debug.Notification("GenerateFollower : " + (i + 1) + " / " + n)
			EndIf
		EndIf
		i += 1
	EndWhile

	string summary = "Exportes : " + done + "   deja faits : " + skipped + "   echecs : " + failed
	MiscUtil.WriteToFile(LogFile, summary + "\n", true, true)
	Debug.MessageBox("GenerateFollower : export termine.\n" + summary + "\nQuitte SANS sauvegarder, puis lance : GenerateFollower build")
EndFunction

bool Function ExportOne(Actor player, Race r, int female, string preset, string outName)
	If r && player.GetRace() != r
		player.SetRace(r)
		Utility.Wait(1.5)
	EndIf
	If player.GetActorBase().GetSex() != female
		ConsoleUtil.ExecuteCommand("player.sexchange")
		Utility.Wait(1.5)
	EndIf
	If !CharGen.LoadPreset(preset)
		return false
	EndIf
	Utility.Wait(1.0)
	player.QueueNiNodeUpdate()
	Utility.Wait(2.0)
	CharGen.ExportHead(outName)
	Utility.Wait(0.5)
	return MiscUtil.FileExists("Data/SKSE/Plugins/CharGen/" + outName + ".nif")
EndFunction
