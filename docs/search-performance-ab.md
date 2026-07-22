# 搜索性能 A/B 消融基准

## 目的

`ListaryOpen.SearchBenchmark ab` 对每项搜索优化运行等价 baseline 和优化实现。计时前会比较有序结果、分数、匹配原因或数据库聚合快照；任何语义差异都会令进程以非零状态退出。

不要直接对唯一的用户索引运行此工具。A/B 模式会把数据库迁移到当前 schema，并确保性能索引存在，应使用数据库副本。

## 可重复命令

```powershell
dotnet build tools/ListaryOpen.SearchBenchmark/ListaryOpen.SearchBenchmark.csproj `
  -c Release --no-restore --nologo

$source = "artifacts\ListaryOpen-win-x64\data\index.db"
$copy = Join-Path $env:TEMP "ListaryOpen-ab-real.db"
Copy-Item $source $copy -Force

tools\ListaryOpen.SearchBenchmark\bin\Release\net8.0-windows\ListaryOpen.SearchBenchmark.exe `
  ab $copy `
  --root "C:\Users\paulx\OneDrive\projects\listary_open" `
  --query open `
  --iterations 5 `
  --sample 5000 `
  --upserts 5000

tools\ListaryOpen.SearchBenchmark\bin\Release\net8.0-windows\ListaryOpen.SearchBenchmark.exe `
  ab $copy `
  --root "C:\Users\paulx\OneDrive\projects\listary_open" `
  --query invoice `
  --iterations 3 `
  --sample 5000 `
  --upserts 1000
```

普通端到端查询仍可运行：

```powershell
tools\ListaryOpen.SearchBenchmark\bin\Release\net8.0-windows\ListaryOpen.SearchBenchmark.exe `
  search $copy --root "C:\Users\paulx\OneDrive\projects\listary_open" open invoice hetong
```

## 测试环境与数据

- 日期：2026-07-15
- .NET SDK：8.0.423，Release
- CPU：Intel Core i7-6700K 4.00 GHz
- 内存：64 GiB
- 数据库：903,500 个 `files` 行，4096-byte page，450,266 pages，约 1.84 GB
- 每组交错运行 baseline/optimized，表中为进程报告的中位数；P95 由同一次运行给出

## 结果

`open` 主运行使用 5 次、5000 条排名/拼音样本；prepared upsert 另用 3 次、5000 条记录，并确保两侧都有相同的 parent 索引：

| 消融项 | Baseline 中位数 | 优化中位数 | 加速 | 等价性 |
|---|---:|---:|---:|---|
| 当前目录 parent 索引 | 1547.859 ms | 0.738 ms | 2096.52x | 同一 SQL 与有序 16 行；baseline 强制 `NOT INDEXED` |
| FTS/fallback 条件跳过 | 318.290 ms | 34.497 ms | 9.23x | 完整前 50 条路径、分数、原因一致；读取真实 usage |
| ResultRanker 单遍 | 16.882 ms | 7.901 ms | 2.14x | 完整结果一致 |
| Pinyin 单遍 | 23.425 ms | 5.510 ms | 4.25x | 5000 个 search text 与 score 完全一致 |
| Prepared batch upsert | 1393.439 ms | 1119.186 ms | 1.25x | count、size、search_text 聚合快照一致 |

Prepared command 在该批量写入中降低约 20% 时间，且不改变存储结果。

分配量：

- ResultRanker：8,480,912 B → 4,992,048 B，减少 41.14%。
- Pinyin：18,538,776 B → 5,080,760 B，减少 72.59%。

5000 条真实路径的 `search_text` 表示从 711,200 字符降至 2,621 字符，减少 99.63%。Baseline 是旧的“完整路径 forms + 文件名 forms”，优化值只保存非重复拼音 aliases；原始文本仍由 `full_path`、`name` 和 FTS 对应列提供。

`invoice` 消融中，优化候选管线判定所有 FTS/fallback pass 都仍有必要，因此报告 `not_applicable`，没有为了数据好看而跳过它们；语义检查仍通过。这验证了 fallback 是条件执行，而不是被无条件删除。

### 快速输入取消审计

`ResultRanker` 和浮层当前目录前置过滤现在每 64 个原始记录检查一次查询取消，检查发生在 mode、扩展名等过滤之前，因此“全部记录都被过滤掉”也能及时停止旧查询。新增测试覆盖枚举中取消、全部过滤时取消和正常 token 语义一致。相同真实库、5000 条样本的 5 轮复测中，优化排名中位数为 6.978 ms、P95 为 7.304 ms、分配 4,992,168 B；与此前 7.901 ms / 4,992,048 B 相比没有耗时回退，分配差异约 0.0024%，完整结果仍一致。

## 保留与撤销结论

- 保留 parent-path 复合索引：等价 SQL 的数量级收益明确。
- 保留候选 pass 条件执行：`open` 结果一致并降低约 89% 时间；不足以填满候选的 `invoice` 不跳过。
- 保留 ResultRanker/Pinyin 单遍实现：结果完全一致，同时降低耗时和分配。
- 保留 prepared upsert：本次等价消融收益约 20%，语义一致；它优化索引写入，不属于查询延迟收益。
- 保留 alias-only `search_text`：样本减少 99.63%，原始路径匹配由结构化列承担。
- preferred-root 排名只提升当前目录的直接文件/文件夹，不提升任意深度后代。这与浮层需求一致，也是候选 pass 可以等价跳过的必要不变量；对应回归测试已添加。

## 限制

- 这是本机单数据库数据，不代表所有磁盘、缓存状态或目录分布。
- 强制 `NOT INDEXED` 是 parent 索引的消融，不是旧版本所有 SQL 的逐字复刻；两侧使用完全相同的 direct-child 语义。
- A/B 工具的候选管线只接受普通文本查询。操作符、扩展名和 folder/file 模式由自动化测试覆盖，不在此微基准中混合测量。
- 首次后台创建 90 万行 parent 索引仍有一次性成本；应用通过后台任务构建，并在索引就绪前跳过该数据库 pass，当前目录文件系统快筛仍可用。
