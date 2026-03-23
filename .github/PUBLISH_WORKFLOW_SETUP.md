# VS Marketplace Publishing Workflow Setup

A GitHub Actions workflow has been created to automatically publish new versions of the **VsAgentic** extension to the Visual Studio Marketplace.

## What the Workflow Does

The `publish-vsix.yml` workflow:

1. **Triggers** when you push a git tag matching `v*.*.*` (e.g., `v1.0.0`, `v1.2.3`)
2. **Extracts** the version number from the tag
3. **Updates** the version in `source.extension.vsixmanifest`
4. **Builds** the VSIX package in Release mode
5. **Locates** the generated `.vsix` file
6. **Publishes** it to the VS Marketplace using `VsixPublisher`
7. **Creates** a GitHub Release with the VSIX artifact attached
8. **Outputs** a colorful summary to the workflow logs

## Setup Requirements

### 1. Register Your Publisher on VS Marketplace

Before you can publish, you need a publisher account:

1. Go to [Visual Studio Marketplace Publisher Management](https://marketplace.visualstudio.com/publish)
2. Sign in with your Microsoft account
3. Create a publisher (if you don't have one already)
   - Your publisher must be named `VsAgentic` (as configured in `source.extension.vsixmanifest`)

### 2. Create a Personal Access Token (PAT)

1. In the [Marketplace Publisher Portal](https://marketplace.visualstudio.com/manage), click your profile
2. Generate a **Personal Access Token** with:
   - **Organization**: Select your publisher account
   - **Scopes**: `Manage` (for VSIX publishing)
   - **Expiration**: Choose appropriate duration (e.g., 1 year)
3. **Copy the token** — you'll need it immediately

### 3. Add the PAT as a GitHub Secret

1. Go to your GitHub repository
2. **Settings** → **Secrets and variables** → **Actions**
3. Click **New repository secret**
4. **Name**: `MARKETPLACE_PAT`
5. **Value**: Paste your Personal Access Token
6. Click **Add secret**

## How to Use

### Publishing a New Version

1. **Update the version** in your code/release notes (optional — the workflow will update the manifest)
2. **Create and push a git tag**:
   ```bash
   git tag v1.2.3
   git push origin v1.2.3
   ```
   - Tags must match semantic versioning: `v*.*.*`

3. **Watch the workflow** in GitHub Actions:
   - Go to **Actions** tab
   - Click the **"Publish VSIX to VS Marketplace"** workflow
   - Monitor the run for any errors

4. **Verify the publication**:
   - Check the [VS Marketplace extension page](https://marketplace.visualstudio.com/items?itemName=VsAgentic.VsAgentic)
   - Check the [GitHub Release](https://github.com/YOUR_ORG/VsAgentic/releases) with the VSIX attached

### Workflow Status

The final step outputs:
- Extension name, version, and VSIX file path
- Links to check on the marketplace and review the release
- All colored in the GitHub Actions logs

## Files Created

- **`.github/workflows/publish-vsix.yml`** — The workflow file
- **`.github/PUBLISH_WORKFLOW_SETUP.md`** — This documentation

## Key Details

| Setting | Value |
|---------|-------|
| **Trigger** | Push tags matching `v*.*.*` |
| **Runner** | `windows-latest` (required for VSIX builds) |
| **Publisher** | `VsAgentic` |
| **Extension ID** | `VsAgentic.VSExtension.c3d4e5f6-a7b8-4c9d-0e1f-2a3b4c5d6e7f` |
| **Secret Required** | `MARKETPLACE_PAT` |

## Troubleshooting

### "VSIX file not found in build output"
- Ensure the build succeeded (check MSBuild logs)
- Verify the output path is correct: `VsAgentic.VSExtension/bin/Release`

### "Invalid tag format"
- Tag must be in format `vX.Y.Z` (e.g., `v1.0.0`)
- No dashes or text after the version number

### "Publish failed"
- Verify the `MARKETPLACE_PAT` secret is set correctly
- Check that your publisher name matches `VsAgentic` in the manifest
- Ensure the version in the manifest is unique (VS Marketplace doesn't allow duplicates)

### "GITHUB_TOKEN error"
- This uses the default `${{ secrets.GITHUB_TOKEN }}`
- It's automatically provided by GitHub Actions
- No additional setup needed

## Future Enhancements

Consider adding:
- Changelog generation from git history
- Version auto-increment logic
- Pre-release detection
- Slack/Teams notifications
- Multiple marketplace support (Visual Studio + VS Code)

## References

- [VsixPublisher Tool](https://learn.microsoft.com/en-us/visualstudio/extensibility/walkthrough-publishing-a-visual-studio-extension-via-command-line)
- [VS Marketplace API](https://docs.microsoft.com/en-us/azure/devops/extend/publish/overview)
- [GitHub Actions Secrets](https://docs.github.com/en/actions/security-guides/using-secrets-in-github-actions)
