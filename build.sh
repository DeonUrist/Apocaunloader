#!/bin/sh
# Builds Apocaunloader.dll against the game's own libraries (mono mcs). Usage: ./build.sh [out.dll]
# Set MANAGED / BEPCORE to your game's Apocalypter_Data\Managed and BepInEx\core folders.
M=${MANAGED:-/e/SteamLibrary/steamapps/common/Apocalypter/Apocalypter_Data/Managed}; B=${BEPCORE:-/e/SteamLibrary/steamapps/common/Apocalypter/BepInEx/core}
mcs -nostdlib -noconfig -target:library -langversion:7 -optimize+ -out:${1:-Apocaunloader.dll} \
  -r:$M/mscorlib.dll -r:$M/System.dll -r:$M/System.Core.dll -r:$M/netstandard.dll \
  -r:$B/BepInEx.dll -r:$B/0Harmony.dll \
  -r:$M/UnityEngine.dll -r:$M/UnityEngine.CoreModule.dll -r:$M/UnityEngine.InputLegacyModule.dll \
  -r:$M/UnityEngine.PhysicsModule.dll -r:$M/UnityEngine.AudioModule.dll -r:$M/Unity.InputSystem.dll \
  -r:$M/PlayMaker.dll -r:$M/Assembly-CSharp.dll \
  Plugin.cs
