{{/*
Lucia Helm Chart Helper Templates

This file contains reusable template functions used throughout the Helm charts.
Include this in other templates with: {{ include "lucia.functionName" . }}
*/}}

{{/*
Expand the name of the chart.
*/}}
{{- define "lucia.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" }}
{{- end }}

{{/*
Create a default fully qualified app name.
*/}}
{{- define "lucia.fullname" -}}
{{- if .Values.fullnameOverride }}
{{- .Values.fullnameOverride | trunc 63 | trimSuffix "-" }}
{{- else }}
{{- $name := default .Chart.Name .Values.nameOverride }}
{{- if contains $name .Release.Name }}
{{- .Release.Name | trunc 63 | trimSuffix "-" }}
{{- else }}
{{- printf "%s-%s" .Release.Name $name | trunc 63 | trimSuffix "-" }}
{{- end }}
{{- end }}
{{- end }}

{{/*
Create chart name and version as used by the chart label.
*/}}
{{- define "lucia.chart" -}}
{{- printf "%s-%s" .Chart.Name .Chart.Version | replace "+" "_" | trunc 63 | trimSuffix "-" }}
{{- end }}

{{/*
Common labels for all resources.
*/}}
{{- define "lucia.labels" -}}
helm.sh/chart: {{ include "lucia.chart" . }}
{{ include "lucia.selectorLabels" . }}
{{- if .Chart.AppVersion }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
{{- end }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
{{- end }}

{{/*
Selector labels for pod selection.
*/}}
{{- define "lucia.selectorLabels" -}}
app.kubernetes.io/name: {{ include "lucia.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end }}

{{/*
Create the name of the service account to use.
*/}}
{{- define "lucia.serviceAccountName" -}}
{{- if .Values.serviceAccount.create }}
{{- default (include "lucia.fullname" .) .Values.serviceAccount.name }}
{{- else }}
{{- default "default" .Values.serviceAccount.name }}
{{- end }}
{{- end }}

{{/*
Redis host for ConfigMap and connection strings.
Returns "hostname" or "hostname.namespace.svc.cluster.local" depending on context.
*/}}
{{- define "lucia.redis.host" -}}
{{- if .Values.redis.enabled }}
{{- printf "%s-redis" (include "lucia.fullname" .) }}
{{- else }}
{{- .Values.redis.externalHost | default "redis" }}
{{- end }}
{{- end }}

{{/*
PostgreSQL host and port for the readiness check: the bundled server, or the external one.
*/}}
{{- define "lucia.postgres.host" -}}
{{- if .Values.postgres.enabled }}
{{- .Values.postgres.host }}
{{- else }}
{{- printf "%s-postgres" (include "lucia.fullname" .) }}
{{- end }}
{{- end }}

{{- define "lucia.postgres.port" -}}
{{- if .Values.postgres.enabled }}
{{- .Values.postgres.port }}
{{- else }}
{{- .Values.bundledPostgres.service.port }}
{{- end }}
{{- end }}

{{/*
PostgreSQL connection strings for every pod using the shared data provider.

External (postgres.enabled=true, secret in postgres.existingSecret):
- If postgres.connectionStringKeys is set, each <db> key names an entry in the
  Secret holding that database's complete Npgsql connection string.
- Otherwise the standard CloudNativePG app-secret layout is assumed
  (host/port/username/password keys). The CNPG_POSTGRES_* parts are declared
  first because Kubernetes only expands $(VAR) from earlier-defined env; each
  ConnectionStrings__<db> then builds its Npgsql string with a per-database
  postgres.databases.<db> name.
Bundled (postgres.enabled=false): the <release>-postgres Secret holds one
complete connection string per database, keyed by database name.
*/}}
{{- define "lucia.postgres.env" -}}
{{- if .Values.postgres.enabled }}
{{- $secret := required "postgres.existingSecret is required when postgres.enabled is true" .Values.postgres.existingSecret }}
{{- if .Values.postgres.connectionStringKeys }}
{{- range $database := list "luciaconfig" "luciatraces" "luciatasks" }}
- name: ConnectionStrings__{{ $database }}
  valueFrom:
    secretKeyRef:
      name: {{ $secret | quote }}
      key: {{ required (printf "postgres.connectionStringKeys.%s is required" $database) (index $.Values.postgres.connectionStringKeys $database) | quote }}
{{- end }}
{{- else }}
- name: CNPG_POSTGRES_HOST
  valueFrom:
    secretKeyRef:
      name: {{ $secret | quote }}
      key: host
- name: CNPG_POSTGRES_PORT
  valueFrom:
    secretKeyRef:
      name: {{ $secret | quote }}
      key: port
- name: CNPG_POSTGRES_USERNAME
  valueFrom:
    secretKeyRef:
      name: {{ $secret | quote }}
      key: username
- name: CNPG_POSTGRES_PASSWORD
  valueFrom:
    secretKeyRef:
      name: {{ $secret | quote }}
      key: password
{{- range $database := list "luciaconfig" "luciatraces" "luciatasks" }}
- name: ConnectionStrings__{{ $database }}
  value: >-
    Host=$(CNPG_POSTGRES_HOST);
    Port=$(CNPG_POSTGRES_PORT);
    Database={{ required (printf "postgres.databases.%s is required" $database) (index $.Values.postgres.databases $database) | quote }};
    Username=$(CNPG_POSTGRES_USERNAME);
    Password=$(CNPG_POSTGRES_PASSWORD);
    SSL Mode={{ $.Values.postgres.sslMode | default "Require" }};
    Trust Server Certificate={{ $.Values.postgres.trustServerCertificate | default "true" }};
{{- end }}
{{- end }}
{{- else }}
{{- range $database := list "luciaconfig" "luciatraces" "luciatasks" }}
- name: ConnectionStrings__{{ $database }}
  valueFrom:
    secretKeyRef:
      name: {{ printf "%s-postgres" (include "lucia.fullname" .) | quote }}
      key: {{ $database | quote }}
{{- end }}
{{- end }}
{{- end }}

{{/*
Format environment variables from map.
Usage: {{ include "lucia.env" . }}
*/}}
{{- define "lucia.env" -}}
{{- range $key, $value := . }}
- name: {{ $key }}
  value: {{ $value | quote }}
{{- end }}
{{- end }}

{{/*
Format environment variables from ConfigMap.
*/}}
{{- define "lucia.envFromConfigMap" -}}
- configMapRef:
    name: {{ include "lucia.fullname" . }}
{{- end }}

{{/*
Format environment variables from Secret.
*/}}
{{- define "lucia.envFromSecret" -}}
- secretRef:
    name: {{ include "lucia.fullname" . }}
{{- end }}

{{/*
Labels for pod disruption budget.
*/}}
{{- define "lucia.podDisruptionBudgetLabels" -}}
{{- include "lucia.labels" . }}
{{- end }}

{{/*
Common pod annotations for checksum verification.
Used to trigger pod restart when ConfigMap or Secret changes.
*/}}
{{- define "lucia.podAnnotations" -}}
checksum/config: {{ include (print $.Template.BasePath "/configmap.yaml") . | sha256sum }}
checksum/secret: {{ include (print $.Template.BasePath "/secret.yaml") . | sha256sum }}
{{- end }}

{{/*
Pod security policy name.
*/}}
{{- define "lucia.pspName" -}}
{{- default (include "lucia.fullname" .) .Values.podSecurityPolicy.name }}
{{- end }}

{{/*
Image pull secrets.
*/}}
{{- define "lucia.imagePullSecrets" -}}
{{- if .Values.imagePullSecrets }}
imagePullSecrets:
{{- range .Values.imagePullSecrets }}
  - name: {{ . }}
{{- end }}
{{- end }}
{{- end }}

{{/*
Kubernetes version check for API compatibility.
*/}}
{{- define "lucia.kubeVersion" -}}
{{- .Capabilities.KubeVersion.GitVersion }}
{{- end }}

{{/*
Return true if Kubernetes API version is >= 1.24.
*/}}
{{- define "lucia.isKube124Plus" -}}
{{- if (semverCompare ">=1.24-0" (include "lucia.kubeVersion" .)) }}true{{ end }}
{{- end }}
