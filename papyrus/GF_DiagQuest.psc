Scriptname GF_DiagQuest extends Quest
{GenerateFollower (build --diag) : pouvoir "GF Diagnostic". Fait apparaitre chaque follower, attend sa tete,
et ecrit dans StorageUtilData/GenerateFollower/diag_result.json si le FaceGen a ete utilise (noeud
GF_FaceGenMarker present), les headparts retenus par le moteur et les formes de notre NIF presentes en jeu.}

Spell Property DiagPower Auto
FormList Property Pages Auto
{Liste de FormLists : toutes les pages du menu d'invocation}

string Property ShapesFile = "GenerateFollower/diag_shapes" AutoReadOnly
string Property ResultFile = "GenerateFollower/diag_result" AutoReadOnly

Event OnInit()
	Actor player = Game.GetPlayer()
	If !player.HasSpell(DiagPower)
		player.AddSpell(DiagPower)
	EndIf
EndEvent

Event OnUpdate()
	Actor player = Game.GetPlayer()
	JsonUtil.ClearAll(ResultFile)
	int total = 0
	int p = 0
	While p < Pages.GetSize()
		FormList lst = Pages.GetAt(p) as FormList
		int i = 0
		While i < lst.GetSize()
			ActorBase base = lst.GetAt(i) as ActorBase
			If base
				Check(base, player)
				total += 1
			EndIf
			i += 1
		EndWhile
		p += 1
	EndWhile
	JsonUtil.Save(ResultFile)
	Debug.MessageBox("GF Diagnostic termine : " + total + " followers. Resultat : SKSE/Plugins/StorageUtilData/GenerateFollower/diag_result.json")
EndEvent

Function Check(ActorBase base, Actor player)
	string id = Math.LogicalAnd(base.GetFormID(), 0xFFF) as string
	Debug.Notification("GF Diagnostic : " + base.GetName())
	Actor ref = player.PlaceAtMe(base, 1, true) as Actor
	int waited = 0
	While !ref.Is3DLoaded() && waited < 30
		Utility.Wait(0.5)
		waited += 1
	EndWhile
	Utility.Wait(2.0)

	Race r = base.GetRace()
	JsonUtil.StringListAdd(ResultFile, id, "nom=" + base.GetName() + " | 3D=" + ref.Is3DLoaded() + " | facegen=" + NetImmerse.HasNode(ref, "GF_FaceGenMarker", false) + " | race=" + r.GetName())

	; headparts du PNJ selon le moteur, avec leurs parties extra
	int n = base.GetNumHeadParts()
	int k = 0
	While k < n
		Part(id, base.GetNthHeadPart(k), r, ref, "")
		k += 1
	EndWhile

	; formes de notre NIF : presentes sur la tete chargee ?
	int s = JsonUtil.StringListCount(ShapesFile, id)
	int j = 0
	While j < s
		string shape = JsonUtil.StringListGet(ShapesFile, id, j)
		JsonUtil.StringListAdd(ResultFile, id, "forme NIF " + shape + " presente=" + NetImmerse.HasNode(ref, shape, false))
		j += 1
	EndWhile

	ref.Disable()
	ref.Delete()
EndFunction

Function Part(string id, HeadPart hp, Race r, Actor ref, string indent)
	If !hp
		JsonUtil.StringListAdd(ResultFile, id, indent + "headpart vide")
		return
	EndIf
	string name = hp.GetPartName()
	FormList valid = hp.GetValidRaces()
	string v = "pas de liste"
	If valid
		v = valid.HasForm(r) as string
	EndIf
	JsonUtil.StringListAdd(ResultFile, id, indent + "headpart " + name + " type=" + hp.GetType() + " valide_race=" + v + " present=" + NetImmerse.HasNode(ref, name, false))
	int e = hp.GetNumExtraParts()
	int i = 0
	While i < e
		Part(id, hp.GetNthExtraPart(i), r, ref, indent + "  ")
		i += 1
	EndWhile
EndFunction
