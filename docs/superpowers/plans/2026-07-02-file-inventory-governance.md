# File Inventory Governance Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the first useful slice of the file server inventory/governance module: basic filesystem scan, storage, summary API, and initial dashboard.

**Architecture:** Keep audit correlation unchanged. Add inventory as a parallel data plane: the agent scans filesystem metadata in batches, API persists snapshots/items, and the web UI reads aggregated inventory metrics. ACL analysis, AD ownership enrichment, recommendations, and hash collection are later phases.

**Tech Stack:** .NET 10 minimal API, SQL Server with manual schema, in-memory fallback repositories, React/Vite frontend, Windows agent.

---

## File Structure

- Create `src/FileServerMonitor.Core/FileInventory.cs`: domain contracts for inventory items, snapshots, summaries, and normalization.
- Modify `src/FileServerMonitor.Api/Program.cs`: register inventory repository, create ingestion/summary endpoints, add SQL Server and in-memory repository implementations.
- Modify `src/FileServerMonitor.Agent/Program.cs`: add optional inventory scan loop and batch submission.
- Modify `src/FileServerMonitor.Agent/appsettings.agent.json`: add disabled-by-default inventory scan settings.
- Modify `src/FileServerMonitor.Web/src/main.tsx`: add the Inventory/Governance tab and data fetch.
- Modify `src/FileServerMonitor.Web/src/styles.css`: add focused styles for the inventory dashboard.
- Modify `tests/FileServerMonitor.Core.Tests/Program.cs`: add normalization and summary tests for inventory.

## Task 1: Core Inventory Contracts

**Files:**
- Create: `/Users/andersonbandeira/Projetos/FileServer/src/FileServerMonitor.Core/FileInventory.cs`
- Modify: `/Users/andersonbandeira/Projetos/FileServer/tests/FileServerMonitor.Core.Tests/Program.cs`

- [ ] **Step 1: Write tests for inventory normalization and summary**

Add tests that verify paths, extensions, depth, file/folder counts, and total bytes.

- [ ] **Step 2: Implement `FileInventory.cs`**

Create records:

- `FileInventoryItemInput`
- `FileInventoryItem`
- `FileInventorySnapshot`
- `FileInventorySummary`
- `FileInventoryTopFolder`
- `FileInventoryTopExtension`
- `FileInventoryNormalizer`
- `FileInventoryAnalyzer`

- [ ] **Step 3: Run Core tests**

Run:

```bash
dotnet run --project /Users/andersonbandeira/Projetos/FileServer/tests/FileServerMonitor.Core.Tests/FileServerMonitor.Core.Tests.csproj --no-restore
```

Expected: all tests pass.

## Task 2: API Inventory Storage And Endpoints

**Files:**
- Modify: `/Users/andersonbandeira/Projetos/FileServer/src/FileServerMonitor.Api/Program.cs`

- [ ] **Step 1: Add repository interface**

Add `IInventoryRepository` with:

- `StartSnapshotAsync`
- `AddBatchAsync`
- `CompleteSnapshotAsync`
- `GetLatestSummaryAsync`
- `GetSnapshotsAsync`

- [ ] **Step 2: Add SQL Server schema**

Create tables:

- `FileInventorySnapshots`
- `FileInventoryItems`

Create indexes:

- latest snapshot by server/share/root/status;
- item lookup by snapshot/path;
- item aggregation by extension;
- item aggregation by parent path.

- [ ] **Step 3: Add in-memory repository**

Provide development fallback matching the SQL interface.

- [ ] **Step 4: Add endpoints**

Add:

- `POST /api/inventory/snapshots/start`
- `POST /api/inventory/snapshots/{id}/items`
- `POST /api/inventory/snapshots/{id}/complete`
- `GET /api/inventory/summary`
- `GET /api/inventory/snapshots`

- [ ] **Step 5: Build API**

Run:

```bash
dotnet build /Users/andersonbandeira/Projetos/FileServer/FileServerMonitor.slnx /p:EnableSqlServer=true
```

Expected: build succeeds.

## Task 3: Agent Basic Scan

**Files:**
- Modify: `/Users/andersonbandeira/Projetos/FileServer/src/FileServerMonitor.Agent/Program.cs`
- Modify: `/Users/andersonbandeira/Projetos/FileServer/src/FileServerMonitor.Agent/appsettings.agent.json`

- [ ] **Step 1: Add inventory settings**

Add settings:

- enabled;
- roots;
- scan interval hours;
- batch size;
- max items per scan;
- include filesystem last access;
- schedule window start/end.

- [ ] **Step 2: Add scan execution**

When enabled and inside the configured window, start a snapshot, enumerate files/folders with `Directory.EnumerateFileSystemEntries`, send batches, and complete the snapshot.

- [ ] **Step 3: Add defensive behavior**

Record scan errors as item errors and continue. Stop scanning when cancellation is requested.

- [ ] **Step 4: Build Agent**

Run:

```bash
dotnet build /Users/andersonbandeira/Projetos/FileServer/FileServerMonitor.slnx /p:EnableSqlServer=true
```

Expected: build succeeds.

## Task 4: Web Dashboard

**Files:**
- Modify: `/Users/andersonbandeira/Projetos/FileServer/src/FileServerMonitor.Web/src/main.tsx`
- Modify: `/Users/andersonbandeira/Projetos/FileServer/src/FileServerMonitor.Web/src/styles.css`

- [ ] **Step 1: Add Inventory tab**

Add a tab named `Inventario` or `Governanca`.

- [ ] **Step 2: Fetch `/api/inventory/summary`**

Show:

- total size;
- total files;
- total folders;
- last scan status/time;
- top folders by size;
- top extensions by size;
- scan errors.

- [ ] **Step 3: Build frontend**

Run:

```bash
npm --prefix /Users/andersonbandeira/Projetos/FileServer/src/FileServerMonitor.Web run build
```

Expected: build succeeds.

## Task 5: End-To-End Verification

**Files:**
- No new files expected.

- [ ] **Step 1: Run Core tests**

```bash
dotnet run --project /Users/andersonbandeira/Projetos/FileServer/tests/FileServerMonitor.Core.Tests/FileServerMonitor.Core.Tests.csproj --no-restore
```

- [ ] **Step 2: Build solution**

```bash
dotnet build /Users/andersonbandeira/Projetos/FileServer/FileServerMonitor.slnx /p:EnableSqlServer=true
```

- [ ] **Step 3: Build web**

```bash
npm --prefix /Users/andersonbandeira/Projetos/FileServer/src/FileServerMonitor.Web run build
```

- [ ] **Step 4: Commit**

```bash
git add /Users/andersonbandeira/Projetos/FileServer
git commit -m "feat: add file inventory governance foundation"
git push origin develop
```
