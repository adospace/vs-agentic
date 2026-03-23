# GitHub Configuration for VsAgentic

This directory contains GitHub Actions workflows and documentation for automated publishing of the VsAgentic extension to the Visual Studio Marketplace.

## Quick Links

### 📖 Documentation (Read in Order)

1. **[QUICK_PUBLISH_GUIDE.md](./QUICK_PUBLISH_GUIDE.md)** ← Start here!
   - Fast setup instructions for first-time publishers
   - Step-by-step release process
   - Common troubleshooting

2. **[WORKFLOW_SUMMARY.md](./WORKFLOW_SUMMARY.md)**
   - Complete overview of the automation
   - Architecture diagram
   - Prerequisites and configuration
   - Detailed workflow steps

3. **[PUBLISH_WORKFLOW_SETUP.md](./PUBLISH_WORKFLOW_SETUP.md)**
   - Comprehensive setup guide
   - Marketplace registration walkthrough
   - GitHub secrets configuration
   - Advanced troubleshooting
   - Future enhancements

### 🤖 Workflow

- **[workflows/publish-vsix.yml](./workflows/publish-vsix.yml)**
  - The GitHub Actions workflow that publishes releases
  - Triggered by git tags matching `v*.*.*`
  - Runs on Windows runners for VSIX/MSBuild support

## Quick Start

```bash
# 1. Register publisher on VS Marketplace (one time)
# Go to: https://marketplace.visualstudio.com/publish

# 2. Generate Personal Access Token
# Go to: https://marketplace.visualstudio.com/manage

# 3. Add GitHub Secret
# Settings → Secrets and variables → Actions → New secret
# Name: MARKETPLACE_PAT
# Value: <your-token>

# 4. Create a release tag and push it
git tag v1.2.3
git push origin v1.2.3

# 5. Watch the workflow in Actions tab
# Then verify on the VS Marketplace
```

## Files in This Directory

```
.github/
├── workflows/
│   └── publish-vsix.yml           # GitHub Actions workflow (110 lines)
├── README.md           # This file
├── QUICK_PUBLISH_GUIDE.md         # Quick reference guide
├── WORKFLOW_SUMMARY.md            # Complete overview
└── PUBLISH_WORKFLOW_SETUP.md  # Detailed setup guide
```

## The Publishing Process

```
You Push Git Tag
    ↓
GitHub Actions Triggered
    ↓
Workflow Runs:
  1. Extract version from tag (v1.2.3 → 1.2.3)
  2. Update manifest file
  3. Build VSIX package
  4. Publish to VS Marketplace
  5. Create GitHub Release
    ↓
Extension Live on Marketplace
```

## Key Configuration

| Setting | Value |
|---------|-------|
| **Workflow File** | `workflows/publish-vsix.yml` |
| **Trigger** | Git tags matching `v*.*.*` |
| **Runner** | Windows (for VSIX/MSBuild) |
| **Publisher** | VsAgentic |
| **Secret Required** | `MARKETPLACE_PAT` |

## Support

- **Issues with the workflow?** Check the [PUBLISH_WORKFLOW_SETUP.md](./PUBLISH_WORKFLOW_SETUP.md#troubleshooting) troubleshooting section
- **First time publishing?** Read [QUICK_PUBLISH_GUIDE.md](./QUICK_PUBLISH_GUIDE.md)
- **Want to understand everything?** See [WORKFLOW_SUMMARY.md](./WORKFLOW_SUMMARY.md)

## References

- [VsixPublisher Documentation](https://learn.microsoft.com/en-us/visualstudio/extensibility/walkthrough-publishing-a-visual-studio-extension-via-command-line)
- [GitHub Actions Documentation](https://docs.github.com/en/actions)
- [VS Marketplace Publishing](https://learn.microsoft.com/en-us/visualstudio/extensibility/publish-extension)

---

**Status**: ✅ Ready to use
**Last Updated**: Now
