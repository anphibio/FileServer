export function isTransientArtifactPath(path: string) {
  const normalized = normalizeArtifactPath(path);
  const leaf = getArtifactLeafName(path).toLowerCase();
  const segments = normalized.split("\\").filter(Boolean);
  const hasTransientContainer = segments.some(isTransientContainerSegment);

  return hasTransientContainer
    || leaf.startsWith("$")
    || leaf.startsWith("~$")
    || leaf === "thumbs.db"
    || leaf === "desktop.ini"
    || leaf.endsWith(".tmp")
    || leaf === "volumejoblock.bin"
    || leaf === "startupprofiledata-noninteractive"
    || /^__psscriptpolicytest_[^.]+\..*\.(?:ps1|psm1)$/.test(leaf)
    || /^optimizationstate\.xml(\.|$)/.test(leaf)
    || leaf === "optimizationscanlog.sl"
    || /^chunkstorestatistics\.xml(\.|$)/.test(leaf)
    || /^dedupstatistics\.xml(\.|$)/.test(leaf)
    || /^changes\.optimization\./.test(leaf)
    || /^\d{8}\.\d{8,9}\.(?:\d{2}\.cd|ccc)$/.test(leaf);
}

export function isTransientContainerSegment(segment: string) {
  return segment === "$recycle.bin"
    || /^\$i[a-z0-9]{5,}$/i.test(segment)
    || /^\$r[a-z0-9]{5,}$/i.test(segment)
    || /^\$[a-z0-9]{7,}$/i.test(segment);
}

function normalizeArtifactPath(path?: string | null) {
  return (path ?? "").trim().replaceAll("/", "\\").replace(/\\+$/, "").toLowerCase();
}

function getArtifactLeafName(path: string) {
  const segments = path.replaceAll("/", "\\").split("\\").filter(Boolean);
  return segments[segments.length - 1] ?? path;
}
