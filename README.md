# RuleMaskDb

RuleMaskDb is a generic data anonymization engine designed to replace sensitive information directly inside a target database using a rule-based configuration file (YAML).
It is ideal for preparing safe, realistic datasets for development, testing, QA, or integration environments.

The tool is datastore-agnostic by design (currently focusing on SQL Server, with RavenDB and others planned).

## Important: RuleMaskDb modifies the target database in place

RuleMaskDb does not clone or copy your database.
It performs anonymization directly on the database you point it to.

👉 Therefore, it must never be executed on a production database.

Recommended workflow
- Create a copy of your production database (backup/restore, dump, snapshot, etc.)
- Restore it in a non-production environment (dev/test/QA)
- Point RuleMaskDb to this target
- Run the anonymization
- Use the anonymized database safely in your workflows

This ensures no real production data is ever exposed.

## How it works

RuleMaskDb uses a YAML configuration file to describe:
- which tables or collections should be processed
- which columns or JSON paths must be anonymized
- what generator should be applied to each field (first name, email, phone, unique values, etc.)

Example configuration:

```yaml
rules:
  - target: sql
    table: Users
    column: FirstName
    generator: firstName

  - target: sql
    table: Users
    column: Email
    generator: emailUnique
```
