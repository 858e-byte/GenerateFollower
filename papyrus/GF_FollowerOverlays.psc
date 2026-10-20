Scriptname GF_FollowerOverlays extends Actor
{GenerateFollower : overlays RaceMenu du preset (tatouages, maquillage, peintures), appliques par NiOverride au chargement.}

; Une entree par valeur NiOverride : noeud ("Body [Ovl0]"...), cle (7 couleur, 8 alpha, 9 texture...), index
; (slot de texture pour la cle 9, -1 sinon) et valeur dans le tableau de son type (cles 0 et 7 : OvlInts,
; cle 9 : OvlStrings, autres : OvlFloats).
string[] Property OvlNodes Auto
int[] Property OvlKeys Auto
int[] Property OvlIndexes Auto
int[] Property OvlInts Auto
float[] Property OvlFloats Auto
string[] Property OvlStrings Auto

; Pas de NiOverride.UpdateModelWeight ni de QueueNiNodeUpdate : ils font regenerer la tete du PNJ (FaceGen perdu,
; visage pale, test 3).
Event OnLoad()
	; bPlayerOnly=1 dans skee64.ini : les PNJ n'ont pas de noeuds d'overlays sans AddOverlays
	If !NiOverride.HasOverlays(self)
		NiOverride.AddOverlays(self)
	EndIf
	bool female = GetActorBase().GetSex() == 1
	int i = 0
	While i < OvlNodes.Length
		int k = OvlKeys[i]
		If k == 9
			NiOverride.AddNodeOverrideString(self, female, OvlNodes[i], k, OvlIndexes[i], OvlStrings[i], true)
		ElseIf k == 0 || k == 7
			NiOverride.AddNodeOverrideInt(self, female, OvlNodes[i], k, OvlIndexes[i], OvlInts[i], true)
		Else
			NiOverride.AddNodeOverrideFloat(self, female, OvlNodes[i], k, OvlIndexes[i], OvlFloats[i], true)
		EndIf
		i += 1
	EndWhile
EndEvent
