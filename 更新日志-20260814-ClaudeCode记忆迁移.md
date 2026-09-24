# 更新日志：Claude Code 记忆迁移接入（2026-08-14 10:40）

## 时间
2026-08-14

## 标题
Claude Code 记忆文件迁移接入 DeepSeek Harness（DSH）

## 背景
本项目此前由 Claude Code（ClaudeCode）维护，记忆体系存放在 `.claude/memory/`（12 个记忆文件 + MEMORY.md 索引），依靠 Claude Code 的会话机制按需加载。现开发工具切换为 DeepSeek Harness，其工作区指令机制会自动注入 `CLAUDE.md`（已确认根目录及 `claudeTestEngineer/CLAUDE.md` 均自动生效），但 `.claude/memory/` 下的记忆文件不会自动加载，需要建立新的索引机制让每个新会话都能发现并读取这些记忆。

## 解决方案
经用户确认采用「原位置不动 + CLAUDE.md 建索引」方案：

1. **记忆文件原位置保留**：`.claude/memory/` 下 12 个记忆文件与 `MEMORY.md` 索引全部不动（保持与 Claude Code 兼容、不破坏 git 历史、继续受版本控制同步到所有设备）；
2. **CLAUDE.md 新增完整记忆索引**：在「## 记忆」章节扩充为 10 条索引（与 MEMORY.md 同步维护），按「协议/算法参考」「架构/决策（必读）」「项目改动记录」三组分类，每条含路径链接 + 一句话要点摘要（关键陷阱如"禁止Contains子串匹配""终态分支用 endDone 标志"直接内联，防遗忘）；
3. **约定读取方式**：每次会话开始先读 `MEMORY.md` 索引（摘要已在 CLAUDE.md 自动注入），需要细节时按需读取对应记忆文件全文（文件含 frontmatter，读取时跳过 `---` 头）。

## 总结
迁移不涉及任何代码与文件移动，仅增强 `CLAUDE.md`（文档改动，无需编译）。今后所有会话（含 DSH）均可通过 CLAUDE.md 自动发现全部项目记忆；记忆更新时同步维护 CLAUDE.md 索引与 MEMORY.md 两处，保持二者一致。
