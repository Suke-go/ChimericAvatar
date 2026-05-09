# Bundled base humanoid VRM (browser-side)

Mirror of `apps/api/avatar-base/`. The dashboard fetches `fem_vroid.vrm`
from `/avatar-base/fem_vroid.vrm` to run naruya/gaussian-vrm preprocessing
in the browser before posting the finished `.gvrm` to the API.

## Required file

`fem_vroid.vrm` — CC0 neutral humanoid from
[madjin/vrm-samples](https://github.com/madjin/vrm-samples).

```
curl -fsSL -o fem_vroid.vrm \
  https://github.com/madjin/vrm-samples/raw/master/vroid/fem_vroid.vrm
```

License: CC0 (verified at <https://github.com/madjin/vrm-samples/blob/master/README.md>).
~11.5 MB, VRM 0.x.
