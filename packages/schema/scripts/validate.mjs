import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import Ajv2020 from "ajv/dist/2020.js";
import addFormats from "ajv-formats";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(__dirname, "..");

const readJson = (relativePath) => {
  const filePath = path.join(root, relativePath);
  return JSON.parse(fs.readFileSync(filePath, "utf8"));
};

const ajv = new Ajv2020({ allErrors: true, strict: true });
addFormats(ajv);

const manifestSchema = readJson("runtime-manifest.schema.json");
const manifestExample = readJson("runtime-manifest.example.json");

const validateManifest = ajv.compile(manifestSchema);
if (!validateManifest(manifestExample)) {
  console.error("runtime-manifest.example.json is invalid:");
  console.error(validateManifest.errors);
  process.exit(1);
}

const gvrmSchema = readJson("gvrm-metadata.schema.json");
ajv.compile(gvrmSchema);

console.log("schema validation ok");
