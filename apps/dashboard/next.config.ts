import type { NextConfig } from "next";
import path from "node:path";

const nextConfig: NextConfig = {
  outputFileTracingRoot: path.join(__dirname, "../.."),
  webpack: (config) => {
    // The vendored naruya/gaussian-vrm code (MIT, in lib/gvrm-vendor/) imports
    // `gaussian-splats-3d` as an unscoped specifier. The npm package we use
    // for the same library is published under @mkkellogg/gaussian-splats-3d,
    // so alias the unscoped name without modifying the vendored source.
    config.resolve = config.resolve ?? {};
    config.resolve.alias = {
      ...(config.resolve.alias as Record<string, string> | undefined),
      "gaussian-splats-3d": "@mkkellogg/gaussian-splats-3d",
    };
    return config;
  },
};

export default nextConfig;
