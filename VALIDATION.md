# 1.3.0 verification

`dotnet run --project verification/LogicTests.csproj -c Release` passes 34 checks covering backpack overflow conservation,
failure/retry behavior, per-item magazine migration, repeated switching, chamber state, serialization and corrupt data.
The existing exhaustive overflow check covers 15,251 count/capacity combinations.

`./verification/RunNative.ps1` passes 26 checks inside the shipped Unity player using the actual PlayMaker and Easy Save
assemblies with synthetic weapon/handler FSMs. It redirects disk save paths to the verification output directory and never
opens a character save. It checks:

- Native arm-model activation and 100 actual FSM switches between identical guns holding 17 and 9 rounds.
- Movement between logical and host slot parents, matching Apocapocket's borrow mechanism.
- Independent hammer state and migration without duplicating the old type pool.
- Switching during an unload and cancelling an unload on character load.
- Easy Save disk/cache roundtrips, save-as, exact slot selection, legacy saves and corrupt metadata preservation.
- Reinserting the magazine snapshot immediately before flushing a rebuilt cache, matching Apocasaver's save mechanism.
- Recreated/dropped items retaining magazines by stable identity, unloading disabled, and save-time activation of all models.
- Installation of all nine production Harmony patches against the installed game assemblies.

The release also builds with Mono's C# 7 compiler against the game's own libraries. These are native integration checks
with synthetic FSMs; a manual playthrough with the game's animations and the complete Apocapocket/Apocasaver flows remains
useful when testing the installed release.
