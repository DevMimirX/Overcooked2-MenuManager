# Compile-time references

The DLLs in `third_party/refs` are compile-time references for the legacy Overcooked! 2 plugin target. They are never copied into build output or release packages.

Sources:

- `0Harmony20.dll` and `BepInEx.dll`: the BepInEx installation used by Overcooked! 2
- `Assembly-CSharp-nstrip.dll`: the existing stripped Overcooked! 2 game reference used by the original mod project
- `UnityEngine*.dll`: the matching Overcooked! 2 managed runtime assemblies

These files are platform inputs, not dependencies on another mod. Replace them only as one compatible set and validate the resulting plugin in the matching game version.
