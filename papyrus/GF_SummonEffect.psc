Scriptname GF_SummonEffect extends ActiveMagicEffect
{GenerateFollower : effet du pouvoir "Invoquer un follower", delegue le menu a la quete principale.}

GF_MainQuest Property Owner Auto

Event OnEffectStart(Actor akTarget, Actor akCaster)
	Owner.RegisterForSingleUpdate(0.1)
EndEvent
