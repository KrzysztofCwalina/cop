# CopWeb Driver REST API Endpoints

The copweb driver is an ASP.NET web service that manages agent tasks. This document describes all available REST API endpoints.

## Base URL
```
http://localhost:5000/api
```

## Endpoints

### 1. Get All Tasks
**GET** `/api/tasks`

Returns a list of all tasks (active and completed).

**Response: 200 OK**
```json
[
  {
    "id": "task-123",
    "specPath": "spec.md",
    "specContent": "# Feature Description...",
    "branch": "feature/task-123",
    "phase": "planning",
    "createdAt": "2024-01-15T10:30:00Z",
    "startedAt": "2024-01-15T10:31:00Z",
    "completedAt": null,
    "elapsed": "00:05:30",
    "isTerminal": false,
    "log": ["Task created", "Phase changed to planning..."]
  }
]
```

---

### 2. Get Task by ID
**GET** `/api/tasks/{id}`

Retrieves a specific task by its ID.

**Parameters:**
- `id` (string, path) - The unique task identifier

**Response: 200 OK**
```json
{
  "id": "task-123",
  "specPath": "spec.md",
  "specContent": "# Feature Description...",
  "branch": "feature/task-123",
  "phase": "planning",
  "createdAt": "2024-01-15T10:30:00Z",
  "startedAt": "2024-01-15T10:31:00Z",
  "completedAt": null,
  "elapsed": "00:05:30",
  "isTerminal": false,
  "log": ["Task created", "Phase changed to planning..."]
}
```

**Response: 404 Not Found**
```json
{}
```

---

### 3. Submit New Task
**POST** `/api/tasks`

Creates and submits a new task with the spec content. The spec content is sent as the request body (plain text).

**Query Parameters:**
- `force` (optional) - Include to force re-submission of duplicate tasks

**Headers:**
- `X-Spec-Path` (optional, default: "spec.md") - Path to the specification file

**Request Body:**
```
# Feature Implementation Specification

## Description
Create a new user authentication module...

## Tasks
- [ ] Design auth schema
- [ ] Implement JWT generation
```

**Response: 201 Created**
```json
{
  "id": "task-123",
  "specPath": "spec.md",
  "specContent": "# Feature Implementation...",
  "branch": "feature/task-123",
  "phase": "submitted",
  "createdAt": "2024-01-15T10:30:00Z",
  "startedAt": null,
  "completedAt": null,
  "elapsed": null,
  "isTerminal": false,
  "log": ["Task submitted"]
}
```

**Response: 409 Conflict** (duplicate task without force flag)
```json
{
  "error": "A task for this spec already exists. Use ?force=true to override."
}
```

**Examples:**

Basic submission:
```bash
curl -X POST http://localhost:5000/api/tasks \
  -H "Content-Type: text/plain" \
  -d @spec.md
```

With custom spec path:
```bash
curl -X POST http://localhost:5000/api/tasks \
  -H "X-Spec-Path: features/auth.md" \
  -d @spec.md
```

Force re-submission:
```bash
curl -X POST "http://localhost:5000/api/tasks?force=true" \
  -d @spec.md
```

---

### 4. Cancel Task
**DELETE** `/api/tasks/{id}`

Cancels a running or pending task.

**Parameters:**
- `id` (string, path) - The task ID to cancel

**Response: 200 OK**
```json
{}
```

**Response: 404 Not Found**
```json
{}
```

**Example:**
```bash
curl -X DELETE http://localhost:5000/api/tasks/task-123
```

---

### 5. Send Feedback to Task
**POST** `/api/tasks/{id}/feedback`

Sends feedback message to a running task. The message is sent as the request body (plain text).

**Parameters:**
- `id` (string, path) - The task ID

**Request Body:**
```
Please implement unit tests for the auth module as well.
```

**Response: 200 OK**
```json
{}
```

**Response: 404 Not Found**
```json
{}
```

**Example:**
```bash
curl -X POST http://localhost:5000/api/tasks/task-123/feedback \
  -d "Feedback message here"
```

---

### 6. Pause Task
**POST** `/api/tasks/{id}/pause`

Pauses a running task.

**Parameters:**
- `id` (string, path) - The task ID

**Response: 200 OK**
```json
{}
```

**Response: 404 Not Found**
```json
{}
```

**Example:**
```bash
curl -X POST http://localhost:5000/api/tasks/task-123/pause
```

---

### 7. Resume Task
**POST** `/api/tasks/{id}/resume`

Resumes a paused task. An optional message can be sent in the request body.

**Parameters:**
- `id` (string, path) - The task ID

**Request Body (optional):**
```
Continue implementation with the remaining features.
```

**Response: 200 OK**
```json
{}
```

**Response: 404 Not Found**
```json
{}
```

**Examples:**

Resume without message:
```bash
curl -X POST http://localhost:5000/api/tasks/task-123/resume
```

Resume with message:
```bash
curl -X POST http://localhost:5000/api/tasks/task-123/resume \
  -d "Resume with enhanced logging"
```

---

### 8. Get Task Logs
**GET** `/api/tasks/{id}/logs`

Retrieves the complete log history for a task.

**Parameters:**
- `id` (string, path) - The task ID

**Response: 200 OK**
```json
[
  "Task submitted at 2024-01-15T10:30:00Z",
  "Phase changed to planning",
  "Analyzing specification...",
  "Planning phase completed",
  "Phase changed to implementation",
  "Creating feature branch..."
]
```

**Response: 404 Not Found**
```json
{}
```

**Example:**
```bash
curl http://localhost:5000/api/tasks/task-123/logs
```

---

## Task States

Tasks progress through the following phases:

- **submitted** - Task has been received but not yet started
- **planning** - Task is in planning/analysis phase
- **implementation** - Task is being implemented
- **testing** - Task is in testing phase
- **review** - Task is under review
- **completed** - Task has been completed successfully
- **failed** - Task failed during execution
- **cancelled** - Task was cancelled by user

## Error Handling

### Common HTTP Status Codes

- **200 OK** - Request succeeded
- **201 Created** - Resource created successfully
- **404 Not Found** - Task not found
- **409 Conflict** - Duplicate task (on POST /api/tasks)
- **500 Internal Server Error** - Server error occurred

### Error Response Format

Conflict errors include an error message:
```json
{
  "error": "Error description"
}
```

## Configuration

The API is configured with JSON serialization that converts enums to string names rather than numeric values, ensuring readable phase and status values in all responses.

## Notes

- All timestamps are in ISO 8601 format (UTC)
- Spec content is sent/received as plain text in request/response bodies
- Task IDs are automatically generated and unique
- The `X-Spec-Path` header helps identify related tasks (defaults to "spec.md")
