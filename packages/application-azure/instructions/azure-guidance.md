# Azure Application Development Guidance

## Resource Provisioning with ARM and Bicep

### ARM Templates
Use Azure Resource Manager (ARM) templates for infrastructure-as-code. Define resources in JSON format with parameters for environment-specific values. ARM templates support conditional deployment, loops, and reusable linked templates. Store templates in source control with environment-specific parameter files.

### Bicep Language
Bicep provides a more concise syntax for ARM templates with better readability. Bicep compiles to ARM JSON. Use Bicep for new projects to reduce verbosity and improve maintainability. Leverage Bicep modules for reusable resource definitions across projects.

## App Service Deployment

### Deployment Slots
Use deployment slots for blue-green deployments and zero-downtime updates. Deploy to staging slots, validate, then swap to production. Maintain separate configuration for each slot. Slots share App Service Plan resources but maintain independent state.

### Application Settings and Configuration
Externalize configuration using App Settings or Azure Key Vault. Avoid hardcoding connection strings and secrets. Use environment-specific settings files and deployment-time substitution. Support local development through user secrets or local configuration files.

## Monitoring and Observability

### Azure Monitor and Application Insights
Configure Application Insights for application performance monitoring (APM). Track request dependencies, exceptions, and custom events. Set up alerts for performance degradation, failure rates, or resource thresholds. Export logs to Log Analytics for long-term retention and analysis.

### Structured Logging
Implement structured logging with correlation IDs for tracing requests across services. Log at appropriate levels (Info, Warning, Error). Include contextual information (user ID, operation ID) in log entries. Use correlation IDs to track distributed operations.

## Security: Managed Identity and Key Vault

### Managed Identity
Use system-assigned or user-assigned managed identities for Azure resource authentication. Eliminate credential management for Azure-to-Azure communication. Assign role-based access control (RBAC) roles to identities based on least privilege principle.

### Azure Key Vault
Store secrets, keys, and certificates in Key Vault. Grant access via managed identities or service principals. Rotate secrets regularly. Audit Key Vault access. Reference Key Vault secrets in App Service configuration without exposing values.

## Environment Configuration

### Multi-Environment Setup
Maintain separate subscriptions or resource groups for dev, test, staging, and production. Use consistent naming conventions with environment tags. Deploy identical infrastructure across environments using the same Bicep/ARM templates. Manage environment-specific settings through parameter files or Key Vault references.
