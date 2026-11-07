import { copyFileSync, mkdirSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const websiteDirectory = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const bootstrapDirectory = resolve(websiteDirectory, "node_modules/bootstrap");
const outputDirectory = resolve(websiteDirectory, "wwwroot/lib/bootstrap/dist");
const licensePath = resolve(websiteDirectory, "wwwroot/lib/bootstrap/LICENSE");

mkdirSync(resolve(outputDirectory, "css"), { recursive: true });
mkdirSync(resolve(outputDirectory, "js"), { recursive: true });

copyFileSync(
  resolve(bootstrapDirectory, "dist/css/bootstrap.min.css"),
  resolve(outputDirectory, "css/bootstrap.min.css")
);
copyFileSync(
  resolve(bootstrapDirectory, "dist/js/bootstrap.bundle.min.js"),
  resolve(outputDirectory, "js/bootstrap.bundle.min.js")
);
copyFileSync(
  resolve(bootstrapDirectory, "LICENSE"),
  licensePath
);
