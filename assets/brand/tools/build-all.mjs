// Rebuilds the whole kit in order: SVGs, PNG and ICO files, social images, test sheets, audit, contrast, verification.
// Run from anywhere: node assets/brand/tools/build-all.mjs   (needs Node 20+ and Microsoft Edge; no other dependency)
import { spawnSync } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const dir = path.dirname(fileURLToPath(import.meta.url));
let failed = false;
for (const step of ['build-svgs.mjs', 'build-raster.mjs', 'build-social.mjs', 'build-sheets.mjs', 'sheets-b.mjs', 'sheets-c.mjs', 'build-integration.mjs', 'svg-audit.mjs', 'contrast.mjs', 'verify.mjs']) {
  process.stdout.write(`== ${step}\n`);
  const r = spawnSync(process.execPath, [path.join(dir, step)], { stdio: 'inherit' });
  if (r.status !== 0) { failed = true; console.error(`${step} exited with ${r.status}`); }
}
process.exit(failed ? 1 : 0);
