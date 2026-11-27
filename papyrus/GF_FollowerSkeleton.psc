Scriptname GF_FollowerSkeleton extends Actor
{GenerateFollower : transformations d'os RaceMenu du preset (echelle de la tete, du buste, des jambes...) et morphs de corps lies a la tete (cou), appliques par NiOverride au chargement.}

; Une entree par transformation : noeud du squelette, cle NiOverride ("RSMPlugin"...), type (30 echelle, 31 position,
; 32 rotation en matrice 3x3, radians) et debut de ses valeurs dans TrValues (1, 3 ou 9 valeurs).
string[] Property TrNodes Auto
string[] Property TrKeys Auto
int[] Property TrTypes Auto
int[] Property TrStarts Auto
float[] Property TrValues Auto

; Morphs de corps lies a la tete (curseurs UBE "NeckSize__HEAD>"...) : la tete exportee a deja le cou modifie, le corps
; doit recevoir le meme morph, sinon une ligne apparait a la jonction. Vides pour un pack construit avant cette version.
string[] Property MorphNames Auto
float[] Property MorphValues Auto

; Memes cles que RaceMenu : reappliquees a chaque chargement, sans effet si elles sont deja en place.
Event OnLoad()
	bool female = GetActorBase().GetSex() == 1
	int i = 0
	int k
	While i < TrNodes.Length
		int s = TrStarts[i]
		If TrTypes[i] == 30
			NiOverride.AddNodeTransformScale(self, false, female, TrNodes[i], TrKeys[i], TrValues[s])
		ElseIf TrTypes[i] == 31
			float[] pos = new float[3]
			k = 0
			While k < 3
				pos[k] = TrValues[s + k]
				k += 1
			EndWhile
			NiOverride.AddNodeTransformPosition(self, false, female, TrNodes[i], TrKeys[i], pos)
		ElseIf TrTypes[i] == 32
			float[] rot = new float[9]
			k = 0
			While k < 9
				rot[k] = TrValues[s + k]
				k += 1
			EndWhile
			NiOverride.AddNodeTransformRotation(self, false, female, TrNodes[i], TrKeys[i], rot)
		EndIf
		NiOverride.UpdateNodeTransform(self, false, female, TrNodes[i])
		i += 1
	EndWhile
	; morphs du cou : sous notre propre cle (OBody ne touche que la sienne) ; le corps n'est rafraichi que si une valeur
	; change, donc une seule fois par partie en pratique
	bool changed = false
	i = 0
	While i < MorphNames.Length
		If NiOverride.GetBodyMorph(self, MorphNames[i], "GenerateFollower") != MorphValues[i]
			NiOverride.SetBodyMorph(self, MorphNames[i], "GenerateFollower", MorphValues[i])
			changed = true
		EndIf
		i += 1
	EndWhile
	If changed
		NiOverride.UpdateModelWeight(self)
	EndIf
EndEvent
