import test from "node:test";
import assert from "node:assert/strict";

import { isTransientArtifactPath } from "./event-noise.ts";

test("treats known dedup and optimization artifacts as transient noise", () => {
  const noisyPaths = [
    "E:\\Corporativo\\dedupStatistics.xml",
    "E:\\Corporativo\\dedupStatistics.xml.old",
    "E:\\Corporativo\\dedupStatistics.xml.new",
    "E:\\Corporativo\\dedupStatistics.xml.alt",
    "E:\\Corporativo\\optimizationScanLog.sl",
    "E:\\Corporativo\\00000007.00000000.01.cd",
    "E:\\Corporativo\\00000007.00000000.02.cd",
    "E:\\Corporativo\\00000003.000000001.ccc",
    "E:\\Corporativo\\changes.optimization.0.1.active.bin",
    "E:\\Corporativo\\__PSScriptPolicyTest_5ldo0dff.l4g.ps1",
    "E:\\Corporativo\\__PSScriptPolicyTest_hnzbvmwy.gsc.psm1",
    "E:\\Corporativo\\StartupProfileData-NonInteractive"
  ];

  for (const path of noisyPaths) {
    assert.equal(isTransientArtifactPath(path), true, path);
  }
});

test("keeps user-created business files out of transient noise", () => {
  const businessPaths = [
    "E:\\Corporativo\\teste-txt-01.txt",
    "E:\\Corporativo\\teste-bmp-02.bmp",
    "E:\\Corporativo\\teste-pptx-02.pptx",
    "E:\\Corporativo\\teste-xlsx-02.xlsx",
    "E:\\Corporativo\\RH\\teste-xlsx-02.xlsx"
  ];

  for (const path of businessPaths) {
    assert.equal(isTransientArtifactPath(path), false, path);
  }
});
