using System.Text.Json;
using Xunit;
using TaoOps.Server.Services;

namespace TaoOps.Server.Tests;

/// <summary>
/// 分支账补齐批次一：各模块下发参数构造类（OpArgs/Args Build）双向分支——
/// 字段全给与全空各一遍，覆盖全部条件分支；断言按 Agent 侧 parseKeyValues 约定逐项核对。
/// </summary>
public class OpArgsBackfillTests
{
    // ---- FR-A14 迁移 ----

    [Fact]
    public void MigrateOpArgs_AllFieldsSet_EmitsEveryKey()
    {
        var args = MigrateOpArgs.Build(new MigrateOpRequest("plan", "/src", "/dst", "app1",
            "f.tar.gz", "m.json", "v1.2", true, false, 0));
        Assert.Equal(new[] {
            "op=plan", "source_path=/src", "target_path=/dst", "name=app1",
            "file=f.tar.gz", "mapping=m.json", "source_version=v1.2",
            "confirm=true", "force=false",
        }, args);
    }

    [Fact]
    public void MigrateOpArgs_AllFieldsEmpty_EmitsOpOnly()
    {
        var args = MigrateOpArgs.Build(new MigrateOpRequest("check", null, null, null,
            null, null, null, null, null, 0));
        Assert.Equal(new[] { "op=check" }, args);
    }

    // ---- FR-A18 负载均衡 ----

    [Fact]
    public void LbOpArgs_AllFieldsSet_EmitsEveryKey()
    {
        var args = LbOpArgs.Build(new LbOpRequest("add", "pool1", "10.0.0.5", 8080, 3,
            true, "http", "/health", 0));
        Assert.Equal(new[] {
            "op=add", "name=pool1", "host=10.0.0.5", "port=8080", "weight=3",
            "backup=true", "health_mode=http", "health_path=/health",
        }, args);
    }

    [Fact]
    public void LbOpArgs_AllFieldsEmpty_EmitsOpOnly()
    {
        var args = LbOpArgs.Build(new LbOpRequest("list", null, null, null, null,
            null, null, null, 0));
        Assert.Equal(new[] { "op=list" }, args);
    }

    // ---- FR-A12 网站统计 ----

    [Fact]
    public void WebStatsOpArgs_AllFieldsSet_EmitsEveryKey()
    {
        var args = WebStatsOpArgs.Build(new WebStatsOpRequest("summary", "mysite", "/blog",
            "2026-11-01", "2026-11-30", 0));
        Assert.Equal(new[] {
            "op=summary", "site=mysite", "path=/blog", "from=2026-11-01", "to=2026-11-30",
        }, args);
    }

    [Fact]
    public void WebStatsOpArgs_AllFieldsEmpty_EmitsOpOnly()
    {
        var args = WebStatsOpArgs.Build(new WebStatsOpRequest("sites", null, null, null, null, 0));
        Assert.Equal(new[] { "op=sites" }, args);
    }

    // ---- 计划任务 ----

    [Fact]
    public void CronOpArgs_AllFieldsSet_EmitsEveryKey()
    {
        var args = CronOpArgs.Build(new CronOpRequest("add", "nightly", "0 2 * * *", "backup.sh", 0));
        Assert.Equal(new[] { "op=add", "name=nightly", "schedule=0 2 * * *", "command=backup.sh" }, args);
    }

    [Fact]
    public void CronOpArgs_AllFieldsEmpty_EmitsOpOnly()
    {
        var args = CronOpArgs.Build(new CronOpRequest("list", null, null, null, 0));
        Assert.Equal(new[] { "op=list" }, args);
    }

    // ---- 防火墙 ----

    [Fact]
    public void FwOpArgs_AllFieldsSet_EmitsEveryKey()
    {
        var args = FwOpArgs.Build(new FwOpRequest("open", 443, "tcp", 0));
        Assert.Equal(new[] { "op=open", "port=443", "proto=tcp" }, args);
    }

    [Fact]
    public void FwOpArgs_AllFieldsEmpty_EmitsOpOnly()
    {
        var args = FwOpArgs.Build(new FwOpRequest("list", null, null, 0));
        Assert.Equal(new[] { "op=list" }, args);
    }

    // ---- 网站 ----

    [Fact]
    public void SiteOpArgs_AllFieldsSet_EmitsEveryKey()
    {
        var args = SiteOpArgs.Build(new SiteOpRequest("create", "shop", "a.com,b.com", "on", 8443, 0));
        Assert.Equal(new[] {
            "op=create", "name=shop", "domains=a.com,b.com", "proxy=on", "port=8443",
        }, args);
    }

    [Fact]
    public void SiteOpArgs_AllFieldsEmpty_EmitsOpOnly()
    {
        var args = SiteOpArgs.Build(new SiteOpRequest("list", null, null, null, null, 0));
        Assert.Equal(new[] { "op=list" }, args);
    }

    // ---- FR-A27 数据库外访 ----

    [Fact]
    public void DbGrantArgs_AllFieldsSet_EmitsEveryKey()
    {
        var args = DbGrantArgs.Build(new DbGrantRequest("db_grant_add", "shopdb", "app",
            "%", new List<string> { "SELECT", "INSERT" }, true, 0));
        Assert.Equal(new[] {
            "op=db_grant_add", "database=shopdb", "user=app", "host=%",
            "privileges=SELECT,INSERT", "confirm=true",
        }, args);
    }

    [Fact]
    public void DbGrantArgs_EmptyListAndFalse_BothBranches()
    {
        // 空权限列表走 Count:0 分支；confirm=false 走三元另一侧。
        var args = DbGrantArgs.Build(new DbGrantRequest("db_grant_revoke", null, null, null,
            new List<string>(), false, 0));
        Assert.Equal(new[] { "op=db_grant_revoke", "confirm=false" }, args);
    }

    [Fact]
    public void DbGrantArgs_NullPrivileges_EmitsOpOnly()
    {
        var args = DbGrantArgs.Build(new DbGrantRequest("db_grant_list", null, null, null, null, null, 0));
        Assert.Equal(new[] { "op=db_grant_list" }, args);
    }

    // ---- FR-A15 复制 ----

    [Fact]
    public void DbReplicaArgs_AllFieldsSet_EmitsEveryKey()
    {
        var args = DbReplicaArgs.Build(new DbReplicaRequest("replica_config", "rep1", "master1",
            "3306", "repl", "pw", "binlog.0001", 1234, 0));
        Assert.Equal(new[] {
            "op=replica_config", "replica_host=rep1", "master_host=master1", "master_port=3306",
            "master_user=repl", "master_password=pw", "master_log_file=binlog.0001", "master_log_pos=1234",
        }, args);
    }

    [Fact]
    public void DbReplicaArgs_AllFieldsEmpty_EmitsOpOnly()
    {
        var args = DbReplicaArgs.Build(new DbReplicaRequest("replica_status", null, null, null,
            null, null, null, null, 0));
        Assert.Equal(new[] { "op=replica_status" }, args);
    }

    // ---- 数据库基础操作 ----

    [Fact]
    public void DbOpArgs_AllFieldsSet_EmitsEveryKey()
    {
        var args = DbOpArgs.Build(new DbOpRequest("restore", "mysql", "shopdb", "/tmp/dump.sql", 0));
        Assert.Equal(new[] { "op=restore", "engine=mysql", "name=shopdb", "dump=/tmp/dump.sql" }, args);
    }

    [Fact]
    public void DbOpArgs_AllFieldsEmpty_EmitsOpOnly()
    {
        var args = DbOpArgs.Build(new DbOpRequest("list", null, null, null, 0));
        Assert.Equal(new[] { "op=list" }, args);
    }
}
