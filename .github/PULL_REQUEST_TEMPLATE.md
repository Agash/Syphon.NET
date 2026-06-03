# Summary

<!-- What does this change and why? -->

## Checklist

- [ ] `dotnet build` is clean (warnings are errors)
- [ ] `dotnet test --filter "TestCategory!=RequiresMetal"` passes
- [ ] If the native surface changed, `native/build-native.sh` was re-run and the C ABI, shim, and managed P/Invoke stay in sync
- [ ] Public API changes are documented
