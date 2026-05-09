# Bundled base humanoid VRM

When a user uploads a Scaniverse PLY/SPZ scan without supplying their own base
VRM, `avatar_build.py` reads the default skeleton from this directory.

## Required file

`fem_vroid.vrm` — CC0 neutral humanoid from
[madjin/vrm-samples](https://github.com/madjin/vrm-samples).

```
curl -fsSL -o fem_vroid.vrm \
  https://github.com/madjin/vrm-samples/raw/master/vroid/fem_vroid.vrm
```

License: CC0 (verified at <https://github.com/madjin/vrm-samples/blob/master/README.md>).
~11.5 MB, VRM 0.x.

## Why this lives outside the source tree

The file is binary and ~11 MB; we don't commit it to git. CI / Docker build
should run the curl above (or vendor the file via a release asset). Override
the location with `AVATAR_BASE_DIR` and `AVATAR_DEFAULT_VRM_FILENAME` env vars.

## Custom override at runtime

End users can still upload their own humanoid VRM via the dashboard's
**Advanced // CUSTOM VRM** section. When `AvatarConfig.source_vrm_asset_id`
is set, that asset wins over the bundled default for this session.
