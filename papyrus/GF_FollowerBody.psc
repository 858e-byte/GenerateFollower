Scriptname GF_FollowerBody extends Actor
{GenerateFollower : preset de corps BodySlide choisi dans le Builder, attribue par OBody NG au premier chargement.}

string Property BodyPreset Auto
{Nom du preset (attribut name du .xml de CalienteTools\BodySlide\SliderPresets)}

; Une seule fois par follower (GF_BodyPreset) : un preset choisi ensuite par le joueur dans le menu d'OBody est respecte.
; La config d'OBody (OBody_presetDistributionConfig.json) n'est pas touchee : l'attribution est gardee par OBody dans
; la sauvegarde (cosave). Sans OBody NG, ou preset absent : rien (nouvel essai au prochain chargement).
Event OnLoad()
	If StorageUtil.GetIntValue(self, "GF_BodyPreset") != 0 || Game.GetModByName("OBody.esp") == 255
		return
	EndIf
	If OBodyNative.AssignPresetToActor(self, BodyPreset)
		StorageUtil.SetIntValue(self, "GF_BodyPreset", 1)
	EndIf
EndEvent
