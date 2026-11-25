namespace TaoOps.Server.Services.K8s;

/// <summary>
/// FR-A28 K8s 侧模板目录：与 FR-A24 Agent 侧模板库（agent/internal/modules/
/// appdeploy/templates.go）同源同字段，供清单渲染共用。K8s 层中控直连、
/// Agent 不参与（设计口径），故此处内嵌同源副本；模板变更需两侧同步。
/// </summary>
public static class K8sTemplateCatalog
{
    public sealed record K8sService(
        string Name, string Image, string[] Ports, Dictionary<string, string> Env,
        string[] Volumes, string HealthCmd, int HealthIntervalSec);

    public sealed record K8sTemplate(string Id, string Name, string Arch, string Kind, K8sService[] Services);

    private static readonly Dictionary<string, string[]> PortMaps = new()
    {
        ["nginx"] = ["80:80"],
        ["redis"] = ["6379:6379"],
        ["mysql"] = ["3306:3306"],
        ["postgres"] = ["5432:5432"],
        ["wordpress"] = [], // db 无宿主端口；wordpress 8080:80
        ["gitea"] = ["3000:3000", "2222:22"],
        ["memcached"] = ["11211:11211"],
        ["rabbitmq"] = ["5672:5672", "15672:15672"],
        ["mongo"] = ["27017:27017"],
    };

    /// <summary>内置模板库（与 Agent 侧 templates.go 同源）。</summary>
    public static readonly K8sTemplate[] Templates =
    [
        new("nginx", "Nginx 网页服务器", "amd64+arm64", "single",
            [new("nginx", "nginx:stable", PortMaps["nginx"], new Dictionary<string, string>(),
                ["nginx-conf:/etc/nginx/conf.d"], "curl -sf http://localhost/ || exit 1", 30)]),
        new("redis", "Redis 缓存", "amd64+arm64", "single",
            [new("redis", "redis:7", PortMaps["redis"], new Dictionary<string, string>(),
                [], "redis-cli ping | grep -q PONG", 15)]),
        new("mysql", "MySQL 数据库", "amd64+arm64", "single",
            [new("mysql", "mysql:8", PortMaps["mysql"],
                new Dictionary<string, string> { ["MYSQL_ROOT_PASSWORD"] = "changeit" },
                ["mysql-data:/var/lib/mysql"], "mysqladmin ping -h localhost -p$MYSQL_ROOT_PASSWORD", 30)]),
        new("postgres", "PostgreSQL 数据库", "amd64+arm64", "single",
            [new("postgres", "postgres:16", PortMaps["postgres"],
                new Dictionary<string, string> { ["POSTGRES_PASSWORD"] = "changeit" },
                ["pg-data:/var/lib/postgresql/data"], "pg_isready -U postgres", 30)]),
        new("wordpress", "WordPress 博客（含数据库）", "amd64+arm64", "stack",
        [
            new("db", "mariadb:11", [],
                new Dictionary<string, string>
                {
                    ["MYSQL_ROOT_PASSWORD"] = "changeit", ["MYSQL_DATABASE"] = "wordpress",
                    ["MYSQL_USER"] = "wp", ["MYSQL_PASSWORD"] = "changeit",
                },
                ["wp-db:/var/lib/mysql"], "healthcheck.sh --connect --innodb_initialized", 30),
            new("wordpress", "wordpress:6", ["8080:80"],
                new Dictionary<string, string>
                {
                    ["WORDPRESS_DB_HOST"] = "db", ["WORDPRESS_DB_USER"] = "wp",
                    ["WORDPRESS_DB_PASSWORD"] = "changeit", ["WORDPRESS_DB_NAME"] = "wordpress",
                },
                ["wp-data:/var/www/html"], "curl -sf http://localhost/ || exit 1", 30),
        ]),
        new("gitea", "Gitea 代码托管", "amd64+arm64", "single",
            [new("gitea", "gitea/gitea:1", PortMaps["gitea"], new Dictionary<string, string>(),
                ["gitea-data:/data"], "curl -sf http://localhost:3000/api/healthz || exit 1", 30)]),
        new("memcached", "Memcached 缓存", "amd64+arm64", "single",
            [new("memcached", "memcached:1", PortMaps["memcached"], new Dictionary<string, string>(),
                [], "echo version | nc localhost 11211 | grep -q VERSION", 15)]),
        new("rabbitmq", "RabbitMQ 消息队列", "amd64+arm64", "single",
            [new("rabbitmq", "rabbitmq:3-management", PortMaps["rabbitmq"], new Dictionary<string, string>(),
                ["rmq-data:/var/lib/rabbitmq"], "rabbitmq-diagnostics -q ping", 30)]),
        new("mongo", "MongoDB 文档数据库", "amd64+arm64", "single",
            [new("mongo", "mongo:7", PortMaps["mongo"], new Dictionary<string, string>(),
                ["mongo-data:/data/db"], "mongosh --quiet --eval 'db.runCommand({ping:1}).ok' | grep -q 1", 30)]),
    ];

    public static K8sTemplate? Get(string id) =>
        Templates.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.Ordinal));
}
