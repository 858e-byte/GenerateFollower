Scriptname GF_DiagEffect extends ActiveMagicEffect
{GenerateFollower (build --diag) : effet du pouvoir "GF Diagnostic", delegue a la quete de diagnostic.}

GF_DiagQuest Property Owner Auto

Event OnEffectStart(Actor akTarget, Actor akCaster)
	Owner.RegisterForSingleUpdate(0.1)
EndEvent
