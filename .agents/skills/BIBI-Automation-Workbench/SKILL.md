```markdown
# BIBI-Automation-Workbench Development Patterns

> Auto-generated skill from repository analysis

## Overview
This skill teaches the core development patterns and conventions used in the BIBI-Automation-Workbench repository. The project is written in TypeScript and does not use a framework, focusing on clear file organization, consistent code style, and conventional commit messages. This guide will help you contribute code, write tests, and follow established workflows in this codebase.

## Coding Conventions

### File Naming
- Use **kebab-case** for all file names.
  - Example:  
    ```
    automation-runner.ts
    user-config.test.ts
    ```

### Import Style
- Use **relative imports** for referencing local modules.
  - Example:
    ```typescript
    import { runTask } from './task-runner';
    ```

### Export Style
- Use **named exports** for all modules.
  - Example:
    ```typescript
    // In task-runner.ts
    export function runTask() { ... }
    ```

### Commit Messages
- Follow **conventional commit** style.
- Use prefixes such as `docs`, `chore`, `ci`.
- Keep commit messages concise (average ~36 characters).
  - Example:
    ```
    docs: update README with usage examples
    chore: refactor import paths
    ci: add GitHub Actions workflow
    ```

## Workflows

### Documentation Updates
**Trigger:** When updating or improving documentation files.
**Command:** `/docs-update`

1. Edit the relevant documentation files (e.g., `README.md`).
2. Use the `docs:` prefix in your commit message.
3. Submit a pull request for review.

### Chore Tasks
**Trigger:** When performing maintenance or refactoring tasks that do not affect functionality.
**Command:** `/chore-task`

1. Make the necessary code changes (e.g., updating dependencies, refactoring).
2. Use the `chore:` prefix in your commit message.
3. Push your changes and create a pull request.

### Continuous Integration Changes
**Trigger:** When modifying CI configuration files or workflows.
**Command:** `/ci-update`

1. Update the relevant CI configuration files.
2. Use the `ci:` prefix in your commit message.
3. Push your changes and open a pull request.

## Testing Patterns

- Test files use the pattern `*.test.*` (e.g., `automation-runner.test.ts`).
- The testing framework is **unknown**; check existing test files for specific patterns.
- Place test files alongside the modules they test or in a dedicated test directory.
- Example test file structure:
  ```
  src/
    automation-runner.ts
    automation-runner.test.ts
  ```

## Commands
| Command        | Purpose                                         |
|----------------|-------------------------------------------------|
| /docs-update   | Start a documentation update workflow           |
| /chore-task    | Begin a maintenance or refactoring workflow     |
| /ci-update     | Initiate a CI configuration update workflow     |
```
