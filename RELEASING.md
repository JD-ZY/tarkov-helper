# Releasing an update

Anyone running the app auto-checks GitHub Releases on startup (see
`UpdateChecker`/`SelfUpdater` in `TarkovHelper.Core`) and offers to
download+restart if a newer tag is found. For that to work, every release
must follow this exact shape:

1. **Bump the version** in `src/TarkovHelper.App/TarkovHelper.App.csproj`
   (`<Version>1.0.0</Version>`). This is what gets compared against the
   release tag - it must be strictly greater than the previous release for
   installed apps to notice.

2. **Build and test**:
   ```
   dotnet test tests/TarkovHelper.Core.Tests/TarkovHelper.Core.Tests.csproj
   ```

3. **Publish** (self-contained, so users don't need .NET installed):
   ```
   dotnet publish src/TarkovHelper.App/TarkovHelper.App.csproj -c Release -r win-x64 --self-contained true -o publish/TarkovHelper
   ```

4. **Zip the publish folder** - the asset name matters, `UpdateChecker`
   looks for a release asset literally named `TarkovHelper.zip`:
   ```powershell
   Compress-Archive -Path "publish\TarkovHelper\*" -DestinationPath "publish\TarkovHelper.zip" -Force
   ```

5. **Commit and push** the version bump and any code changes.

6. **Tag and create the GitHub release** - the tag must be `vX.Y.Z` (with
   the `v` prefix) matching the csproj version, since `UpdateChecker` strips
   a leading `v` before parsing:
   ```
   gh release create vX.Y.Z "publish/TarkovHelper.zip" --title "vX.Y.Z" --notes "What changed."
   ```

That's it - anyone with an older version running will see an update banner
next time they launch the app, and can download + restart into the new
build without reinstalling anything by hand.
