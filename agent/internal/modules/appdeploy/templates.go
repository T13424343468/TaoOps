package appdeploy

// Template is one entry of the built-in one-click deployment library
// (FR-A24). All templates use official multi-arch images (amd64+arm64)
// unless noted; Arch records the verified scope per PRD v1.3 (arm64
// 实弹受限 → 标注).
type Template struct {
	ID       string    `json:"id"`
	Name     string    `json:"name"`
	Arch     string    `json:"arch"` // "amd64+arm64" or "amd64 (arm64 未实弹验证)"
	Kind     string    `json:"kind"` // "single" | "stack"
	Services []Service `json:"services"`
}

// Service is one container inside a template.
type Service struct {
	Name         string            `json:"name"`
	Image        string            `json:"image"`
	Ports        []string          `json:"ports,omitempty"`
	Env          map[string]string `json:"env,omitempty"`
	Volumes      []string          `json:"volumes,omitempty"`
	HealthCmd    string            `json:"health_cmd,omitempty"`    // CMD-SHELL body; empty = no healthcheck
	HealthInterval int             `json:"health_interval_sec,omitempty"`
}

// templates is the first-phase library (PRD: 首期 8~10 模板).
var templates = []Template{
	{
		ID: "nginx", Name: "Nginx 网页服务器", Arch: "amd64+arm64", Kind: "single",
		Services: []Service{{
			Name: "nginx", Image: "nginx:stable",
			Ports: []string{"80:80"}, Volumes: []string{"nginx-conf:/etc/nginx/conf.d"},
			HealthCmd: "curl -sf http://localhost/ || exit 1", HealthInterval: 30,
		}},
	},
	{
		ID: "redis", Name: "Redis 缓存", Arch: "amd64+arm64", Kind: "single",
		Services: []Service{{
			Name: "redis", Image: "redis:7",
			Ports: []string{"6379:6379"},
			HealthCmd: "redis-cli ping | grep -q PONG", HealthInterval: 15,
		}},
	},
	{
		ID: "mysql", Name: "MySQL 数据库", Arch: "amd64+arm64", Kind: "single",
		Services: []Service{{
			Name: "mysql", Image: "mysql:8",
			Ports: []string{"3306:3306"}, Volumes: []string{"mysql-data:/var/lib/mysql"},
			Env: map[string]string{"MYSQL_ROOT_PASSWORD": "changeit"},
			HealthCmd: "mysqladmin ping -h localhost -p$MYSQL_ROOT_PASSWORD", HealthInterval: 30,
		}},
	},
	{
		ID: "postgres", Name: "PostgreSQL 数据库", Arch: "amd64+arm64", Kind: "single",
		Services: []Service{{
			Name: "postgres", Image: "postgres:16",
			Ports: []string{"5432:5432"}, Volumes: []string{"pg-data:/var/lib/postgresql/data"},
			Env: map[string]string{"POSTGRES_PASSWORD": "changeit"},
			HealthCmd: "pg_isready -U postgres", HealthInterval: 30,
		}},
	},
	{
		ID: "wordpress", Name: "WordPress 博客（含数据库）", Arch: "amd64+arm64", Kind: "stack",
		Services: []Service{
			{
				Name: "db", Image: "mariadb:11", Volumes: []string{"wp-db:/var/lib/mysql"},
				Env: map[string]string{"MYSQL_ROOT_PASSWORD": "changeit", "MYSQL_DATABASE": "wordpress", "MYSQL_USER": "wp", "MYSQL_PASSWORD": "changeit"},
				HealthCmd: "healthcheck.sh --connect --innodb_initialized", HealthInterval: 30,
			},
			{
				Name: "wordpress", Image: "wordpress:6",
				Ports: []string{"8080:80"}, Volumes: []string{"wp-data:/var/www/html"},
				Env: map[string]string{"WORDPRESS_DB_HOST": "db", "WORDPRESS_DB_USER": "wp", "WORDPRESS_DB_PASSWORD": "changeit", "WORDPRESS_DB_NAME": "wordpress"},
				HealthCmd: "curl -sf http://localhost/ || exit 1", HealthInterval: 30,
			},
		},
	},
	{
		ID: "gitea", Name: "Gitea 代码托管", Arch: "amd64+arm64", Kind: "single",
		Services: []Service{{
			Name: "gitea", Image: "gitea/gitea:1",
			Ports: []string{"3000:3000", "2222:22"}, Volumes: []string{"gitea-data:/data"},
			HealthCmd: "curl -sf http://localhost:3000/api/healthz || exit 1", HealthInterval: 30,
		}},
	},
	{
		ID: "memcached", Name: "Memcached 缓存", Arch: "amd64+arm64", Kind: "single",
		Services: []Service{{
			Name: "memcached", Image: "memcached:1",
			Ports: []string{"11211:11211"},
			HealthCmd: "echo version | nc localhost 11211 | grep -q VERSION", HealthInterval: 15,
		}},
	},
	{
		ID: "rabbitmq", Name: "RabbitMQ 消息队列", Arch: "amd64+arm64", Kind: "single",
		Services: []Service{{
			Name: "rabbitmq", Image: "rabbitmq:3-management",
			Ports: []string{"5672:5672", "15672:15672"}, Volumes: []string{"rmq-data:/var/lib/rabbitmq"},
			HealthCmd: "rabbitmq-diagnostics -q ping", HealthInterval: 30,
		}},
	},
	{
		ID: "mongo", Name: "MongoDB 文档数据库", Arch: "amd64+arm64", Kind: "single",
		Services: []Service{{
			Name: "mongo", Image: "mongo:7",
			Ports: []string{"27017:27017"}, Volumes: []string{"mongo-data:/data/db"},
			HealthCmd: "mongosh --quiet --eval 'db.runCommand({ping:1}).ok' | grep -q 1", HealthInterval: 30,
		}},
	},
}

// Templates returns the built-in template library.
func Templates() []Template { return templates }

// GetTemplate looks up one template by id.
func GetTemplate(id string) (Template, bool) {
	for _, t := range templates {
		if t.ID == id {
			return t, true
		}
	}
	return Template{}, false
}
