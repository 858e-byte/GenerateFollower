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
string Property RemoveEntry = "[Remove this power]" Auto
string Property RemoveYes = "Yes, remove this summon power" Auto
string Property RemoveWarn = " (followers without a starting location will no longer come)" Auto
string Property RemoveNo = "Cancel" Auto
string Property RemovedText = "Summon power removed." Auto
{Textes de « retirer ce pouvoir » : anglais par defaut (packs construits avant cette entree), langue du Builder sinon}
bool Property NoPower = false Auto
{Pack construit sans pouvoir d'invocation (tous les followers ont un lieu de depart) : le pouvoir n'est pas donne,
et il est retire s'il venait d'une version precedente du pack}

Actor[] Property HomeRefs Auto
{References persistantes des followers (une par follower, dans la cellule du pack) : deplacees par l'invocation et le
lieu de depart au lieu d'en creer avec PlaceAtMe. Vide pour un pack construit avant cette version.}

; followers places : gardes ici pour qu'ils restent persistants
Actor[] Placed

; Chaque reference persistante devient la reference du follower (GF_Ref), sauf si une reference vivante existe deja
; (creee par une version precedente du pack, partie en cours) : on garde celle-la et la reference persistante est
; desactivee, pour ne pas avoir deux fois le meme follower.
Function AdoptHomeRefs()
	If !HomeRefs
		return
	EndIf
	int i = 0
	While i < HomeRefs.Length
		Actor home = HomeRefs[i]
		If home
			ActorBase base = home.GetActorBase()
			Actor ref = StorageUtil.GetFormValue(base, "GF_Ref") as Actor
			If ref == None || (ref != home && ref.IsDead() && !home.IsDead())
				StorageUtil.SetFormValue(base, "GF_Ref", home)
				If StorageUtil.GetIntValue(base, "GF_Placed") == 0
					home.Disable()   ; reste dans sa cellule tant qu'il n'est ni invoque ni place
				EndIf
			ElseIf ref != home && !home.IsDisabled()
				home.Disable()
			EndIf
		EndIf
		i += 1
	EndWhile
EndFunction

Event OnInit()
	AdoptHomeRefs()
	Actor player = Game.GetPlayer()
	If NoPower
		If player.HasSpell(SummonPower)
			player.RemoveSpell(SummonPower)
		EndIf
	ElseIf !player.HasSpell(SummonPower)
		player.AddSpell(SummonPower)
	EndIf
	PlaceAll()
EndEvent

; Cree chaque follower qui a un lieu de depart et le deplace au point d'arrivee de la porte. Une seule fois par follower
; et par lieu (GF_Placed, GF_PlacedAt = porte) : appele aussi au lancement du pouvoir, pour un pack mis a jour en cours
; de partie. Lieu change dans le pack : le follower va au nouveau lieu, sauf s'il accompagne le joueur. Follower place
; par une version sans GF_PlacedAt : son lieu est note sans le deplacer.
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
		If base && anchor
			Actor ref = StorageUtil.GetFormValue(base, "GF_Ref") as Actor
			bool wasPlaced = StorageUtil.GetIntValue(base, "GF_Placed") != 0
			ObjectReference at = StorageUtil.GetFormValue(base, "GF_PlacedAt") as ObjectReference
			If wasPlaced && at == None
				StorageUtil.SetFormValue(base, "GF_PlacedAt", anchor)
			ElseIf !wasPlaced ||(at != anchor && !(ref && !ref.IsDead() && ref.IsPlayerTeammate()))
				If ref == None || ref.IsDead()
					ref = Game.GetPlayer().PlaceAtMe(base, 1, true) as Actor   ; desactive : invisible jusqu'au MoveTo
					StorageUtil.SetFormValue(base, "GF_Ref", ref)
				EndIf
				ref.MoveTo(anchor, PlaceX[i], PlaceY[i], PlaceZ[i], false)
				ref.SetAngle(0.0, 0.0, PlaceAngle[i])
				ref.Enable()
				StorageUtil.SetIntValue(base, "GF_Placed", 1)
				StorageUtil.SetFormValue(base, "GF_PlacedAt", anchor)
			EndIf
		EndIf
		If base && i < Placed.Length
			Placed[i] = StorageUtil.GetFormValue(base, "GF_Ref") as Actor
		EndIf
		i += 1
	EndWhile
EndFunction

; Appele par GF_SummonEffect via RegisterForSingleUpdate, pour ne pas dependre de la duree de l'effet magique
Event OnUpdate()
	AdoptHomeRefs()
	PlaceAll()
	If NoPower
		; pouvoir encore present d'une version precedente : retire
		Game.GetPlayer().RemoveSpell(SummonPower)
		return
	EndIf
	; le premier menu affiche (pages, ou followers s'il n'y a qu'une page) finit par l'entree « retirer ce pouvoir »
	ActorBase base
	If Pages.GetSize() == 1
		base = ChooseFollower(Pages.GetAt(0) as FormList, true)
	Else
		int page = ChoosePage()
		If page < 0
			return
		ElseIf page >= Pages.GetSize()
			AskRemove()
			return
		EndIf
		base = ChooseFollower(Pages.GetAt(page) as FormList, false)
	EndIf
	If base
		Summon(base, Game.GetPlayer())
	EndIf
EndEvent

int Function ChoosePage()
	int count = Pages.GetSize()
	UIListMenu menu = UIExtensions.GetMenu("UIListMenu") as UIListMenu
	int i = 0
	While i < count
		menu.AddEntryItem(PageNames[i])
		i += 1
	EndWhile
	menu.AddEntryItem(RemoveEntry)
	menu.OpenMenu()
	return menu.GetResultInt()
EndFunction

; withRemove : derniere entree « retirer ce pouvoir » (menu affiche meme pour un seul follower)
ActorBase Function ChooseFollower(FormList lst, bool withRemove)
	int count = lst.GetSize()
	If count == 1 && !withRemove
		return lst.GetAt(0) as ActorBase
	EndIf
	UIListMenu menu = UIExtensions.GetMenu("UIListMenu") as UIListMenu
	int i = 0
	While i < count
		menu.AddEntryItem(lst.GetAt(i).GetName())
		i += 1
	EndWhile
	If withRemove
		menu.AddEntryItem(RemoveEntry)
	EndIf
	menu.OpenMenu()
	int choice = menu.GetResultInt()
	If withRemove && choice == count
		AskRemove()
		return None
	ElseIf choice < 0 || choice >= count
		return None
	EndIf
	return lst.GetAt(choice) as ActorBase
EndFunction

; Retire le pouvoir apres confirmation ; avertit si des followers du pack n'ont pas de lieu de depart (ils ne pourront
; plus venir). Pour le recuperer : reconstruire le pack, ou la console (player.addspell).
Function AskRemove()
	UIListMenu menu = UIExtensions.GetMenu("UIListMenu") as UIListMenu
	string yes = RemoveYes
	If HasUnplaced()
		yes = yes + RemoveWarn
	EndIf
	menu.AddEntryItem(yes)
	menu.AddEntryItem(RemoveNo)
	menu.OpenMenu()
	If menu.GetResultInt() == 0
		Game.GetPlayer().RemoveSpell(SummonPower)
		Debug.Notification(RemovedText)
	EndIf
EndFunction

bool Function HasUnplaced()
	int p = 0
	While p < Pages.GetSize()
		FormList lst = Pages.GetAt(p) as FormList
		int i = 0
		While i < lst.GetSize()
			If !PlacedNpcs || PlacedNpcs.Find(lst.GetAt(i)) < 0
				return true
			EndIf
			i += 1
		EndWhile
		p += 1
	EndWhile
	return false
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
