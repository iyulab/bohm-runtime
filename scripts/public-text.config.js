// This repository's own public-text check: the default rules of @iyulab/tauri-kit-dev's
// public-text check (local paths, private hosts), with the tests whose subject is exactly such text.
//
//     npx @iyulab/tauri-kit-dev@0.11.1 public-text --config scripts/public-text.config.js .
//
// It names no one. Anyone contributing from a private workspace checks their own names with their
// own config as well, kept outside this repository.
export default {
  allowed: [
    // A document may name any address; these tests show that local and private ones are refused.
    'tests/Bohm.Runtime.Tests/Assets/AssetCacheTests.cs:',
    // The rule that refuses them names the suffixes it refuses.
    'src/Bohm.Runtime/Assets/AssetCache.cs:',
    // The function that decides whether an address is this computer or a private network names the suffixes it
    // treats as private. Allowed by where its two lines begin, so any other line in the file still counts.
    'src/Bohm.Runtime.Host/Llm/ProviderDeclaration.cs:/// or a name that cannot be reached from outside',
    'src/Bohm.Runtime.Host/Llm/ProviderDeclaration.cs:|| host.EndsWith(".internal"',
    // Made-up home folder of an account named "someone" — the path a person adopted a file from.
    '\\Users\\someone\\',
  ],
}
