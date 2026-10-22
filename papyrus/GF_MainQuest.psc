Scriptname GF_MainQuest extends Quest
{GenerateFollower : donne le pouvoir d'invocation au joueur et gere le menu de choix (UIExtensions).}

Spell Property SummonPower Auto
FormList Property Pages Auto
{Liste de FormLists, une par page du menu}
string[] Property PageNames Auto
string Property SummonedText = " est la. Parle-lui pour le recruter." Auto
{Fin de la notification apres l'invocation, dans la langue du Builder (rempli par le generateur)}

FormList Property PlacedNpcs Auto
{Followers avec un lieu de depart (vide sans lieu choisi)}
ObjectReference[] Property PlaceDoors Auto
{Porte du jeu (reference persistante) pres de laquelle chaque follower apparait}
float[] Property PlaceX Auto
float[] Property PlaceY Auto
float[] Property PlaceZ Auto
float[] Property PlaceAngle Auto

; followers places : gardes ici pour qu'ils restent persistants
Actor[] Placed

Event OnInit()
	Actor player = Game.GetPlayer()
	If !player.HasSpell(SummonPower)
		player.AddSpell(SummonPower)
	EndIf
	PlaceAll()
EndEvent

; Cree chaque follower qui a un lieu de depart et le deplace au point d'arrivee de la porte. Une seule fois par follower
; (GF_Placed) : appele aussi au lancement du pouvoir, pour un pack mis a jour en cours de partie.
Function PlaceAll()
	If !PlacedNpcs
		return
	EndIf
	int count = PlacedNpcs.GetSize()
	If !Placed
		Placed = new Actor[128]   ; taille fixe en Papyrus de base (au-dela : places, mais pas gardes ici)
	EndIf
	int i = 0
	While i < count
		ActorBase base = PlacedNpcs.GetAt(i) as ActorBase
		ObjectReference anchor = PlaceDoors[i]
		If base && anchor && StorageUtil.GetIntValue(base, "GF_Placed") == 0
			Actor ref = StorageUtil.GetFormValue(base, "GF_Ref") as Actor
			If ref == None || ref.IsDead()
				ref = Game.GetPlayer().PlaceAtMe(base, 1, true) as Actor   ; desactive : invisible jusqu'au MoveTo
				StorageUtil.SetFormValue(base, "GF_Ref", ref)
			EndIf
			ref.MoveTo(anchor, PlaceX[i], PlaceY[i], PlaceZ[i], false)
			ref.SetAngle(0.0, 0.0, PlaceAngle[i])
			ref.Enable()
			StorageUtil.SetIntValue(base, "GF_Placed", 1)
		EndIf
		If base && i < Placed.Length
			Placed[i] = StorageUtil.GetFormValue(base, "GF_Ref") as Actor
		EndIf
		i += 1
	EndWhile
EndFunction

; Appele par GF_SummonEffect via RegisterForSingleUpdate, pour ne pas dependre de la duree de l'effet magique
Event OnUpdate()
	PlaceAll()
	int page = ChoosePage()
	If page < 0
		return
	EndIf
	FormList lst = Pages.GetAt(page) as FormList
	ActorBase base = ChooseFollower(lst)
	If base
		Summon(base, Game.GetPlayer())
	EndIf
EndEvent

int Function ChoosePage()
	int count = Pages.GetSize()
	If count == 1
		return 0
	EndIf
	UIListMenu menu = UIExtensions.GetMenu("UIListMenu") as UIListMenu
	int i = 0
	While i < count
		menu.AddEntryItem(PageNames[i])
		i += 1
	EndWhile
	menu.OpenMenu()
	return menu.GetResultInt()
EndFunction

ActorBase Function ChooseFollower(FormList lst)
	int count = lst.GetSize()
	If count == 1
		return lst.GetAt(0) as ActorBase
	EndIf
	UIListMenu menu = UIExtensions.GetMenu("UIListMenu") as UIListMenu
	int i = 0
	While i < count
		menu.AddEntryItem(lst.GetAt(i).GetName())
		i += 1
	EndWhile
	menu.OpenMenu()
	int choice = menu.GetResultInt()
	If choice < 0 || choice >= count
		return None
	EndIf
	return lst.GetAt(choice) as ActorBase
EndFunction

Function Summon(ActorBase base, Actor player)
	Actor ref = StorageUtil.GetFormValue(base, "GF_Ref") as Actor
	If ref == None || ref.IsDead()
		ref = player.PlaceAtMe(base, 1, true) as Actor
		StorageUtil.SetFormValue(base, "GF_Ref", ref)
	Else
		float a = player.GetAngleZ()
		ref.MoveTo(player, 120.0 * Math.Sin(a), 120.0 * Math.Cos(a), 0.0)
		ref.Enable()
	EndIf
	Debug.Notification(base.GetName() + SummonedText)
EndFunction
