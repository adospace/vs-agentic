# Quick Publish Guide

## One-Time Setup (Do This First)

### 1. Register Publisher
- Visit: https://marketplace.visualstudio.com/publish
- Create publisher named **`VsAgentic`**

### 2. Generate PAT Token
- Go to: https://marketplace.visualstudio.com/manage
- Create a Personal Access Token with `Manage` scope
- Copy the token value

### 3. Add to GitHub
1. Go to your repo **Settings** → **Secrets and variables** → **Actions**
2. Click **New repository secret**
3. Name: `MARKETPLACE_PAT`
4. Value: Paste your token
5. Save

## Publishing a New Version

### Every Release

```bash
# Make sure you're on main/master branch
git log -1  # Verify your changes

# Tag the release (must be v#.#.# format)
git tag v1.2.3
git push origin v1.2.3
```

### Then Wait & Verify

1. Go to **Actions** tab in GitHub
2. Watch **"Publish VSIX to VS Marketplace"** run
3. When complete, check:
   - ✅ VS Marketplace: https://marketplace.visualstudio.com/items?itemName=VsAgentic.VsAgentic
   - ✅ GitHub Releases: https://github.com/YOUR_ORG/VsAgentic/releases

## That's It!

The workflow will:
- Extract version from tag (`v1.2.3` → `1.2.3`)
- Update the manifest file
- Build the extension
- Publish to VS Marketplace
- Create a GitHub Release with the VSIX file

## Common Issues

| Problem | Solution |
|---------|----------|
| **VSIX not found** | Ensure build succeeds (check logs) |
| **Publish failed** | Verify `MARKETPLACE_PAT` secret exists |
| **Invalid tag** | Use format `v1.0.0` exactly |

## Files

- Workflow: `.github/workflows/publish-vsix.yml`
- Setup docs: `.github/PUBLISH_WORKFLOW_SETUP.md`
- This guide: `.github/QUICK_PUBLISH_GUIDE.md`
