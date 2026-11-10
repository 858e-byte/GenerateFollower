Scriptname GF_FollowerSkeleton extends Actor
{GenerateFollower : transformations d'os RaceMenu du preset (echelle de la tete, du buste, des jambes...), appliquees par NiOverride au chargement.}

; Une entree par transformation : noeud du squelette, cle NiOverride ("RSMPlugin"...), type (30 echelle, 31 position,
; 32 rotation en matrice 3x3, radians) et debut de ses valeurs dans TrValues (1, 3 ou 9 valeurs).
string[] Property TrNodes Auto
string[] Property TrKeys Auto
int[] Property TrTypes Auto
int[] Property TrStarts Auto
float[] Property TrValues Auto

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
EndEvent
