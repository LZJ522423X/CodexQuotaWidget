# Skill 更新适配器

这里保存小组件调用的更新器源码，不包含任何用户的 settings、provenance、安装清单、凭据或日志。
默认不会安装或更新任何 Skill。只允许已有来源登记及本地文件清单能够交叉核验的更新。

已部署用户可继续使用原 D 盘更新器。其他部署可通过 `CODEX_QUOTA_WIDGET_SKILL_UPDATER` 指定 D 盘普通目录；该目录必须包含 updater.ps1、lib 和经过人工核验的本机 config/settings.json、config/provenance.json。不提供可直接覆盖他人环境的预填来源清单。
需要 PowerShell 7、经过验证的官方 Codex CLI 及网络连接；不会自动安装依赖。
没有可靠来源登记时，检查应报告无法检查或来源不明，不代表已是最新。
独立直接运行脚本可能更新全部符合条件的 Skill；小组件仅使用 CheckOnly 和用户明确勾选后生成的 ApprovedPlan 模式。
