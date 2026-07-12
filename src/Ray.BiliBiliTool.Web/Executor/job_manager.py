import json
import threading
import time
import uuid
from dataclasses import dataclass, field


FINAL_STATUSES = {"done", "error", "stopped", "timeout", "manual_required"}


class JobError(Exception):
    def __init__(self, message, category="unknown", recoverable=True, suggestion="查看日志后重试"):
        super().__init__(message)
        self.message = message
        self.category = category
        self.recoverable = recoverable
        self.suggestion = suggestion


@dataclass
class Job:
    id: str
    action: str
    status: str = "started"
    progress: int = 0
    message: str = "任务已启动"
    result: dict | None = None
    error: str = ""
    category: str = ""
    recoverable: bool = True
    suggestion: str = ""
    stop_requested: bool = False
    events: list = field(default_factory=list)
    created_at: float = field(default_factory=time.time)
    updated_at: float = field(default_factory=time.time)


class JobManager:
    def __init__(self, store=None):
        self.store = store
        self.jobs = {}
        self.lock = threading.Lock()

    def start(self, action, worker, payload=None):
        job = Job(id=f"job-{uuid.uuid4().hex[:12]}", action=action)
        self._save(job)
        thread = threading.Thread(target=self._run, args=(job.id, worker, payload or {}), daemon=True)
        thread.start()
        return job

    def _run(self, job_id, worker, payload):
        job = self.get(job_id)
        if not job:
            return
        if job.stop_requested:
            self.update(job_id, status="stopped", message="任务已停止", event_type="stopped")
            return
        self.update(job_id, status="running", progress=1, message="任务运行中", event_type="progress")
        try:
            result = worker(job, payload)
            fresh = self.get(job_id)
            if fresh and fresh.stop_requested:
                self.update(job_id, status="stopped", progress=fresh.progress, message="任务已停止", event_type="stopped")
                return
            self.update(
                job_id,
                status="done",
                progress=100,
                message="任务完成",
                result=result or {},
                event_type="done",
            )
        except JobError as error:
            status = "manual_required" if error.category == "manual_required" else "error"
            self.update(
                job_id,
                status=status,
                message=error.message,
                error=error.message,
                category=error.category,
                recoverable=error.recoverable,
                suggestion=error.suggestion,
                event_type=status,
            )
        except Exception as error:
            self.update(
                job_id,
                status="error",
                message=str(error),
                error=str(error),
                category="unknown",
                recoverable=True,
                suggestion="查看子项目日志和请求参数后重试",
                event_type="error",
            )

    def update(self, job_id, event_type=None, **changes):
        with self.lock:
            job = self.jobs.get(job_id)
            if not job:
                return None
            for key, value in changes.items():
                setattr(job, key, value)
            job.updated_at = time.time()
            if event_type:
                job.events.append({"event": event_type, "data": self.serialize(job)})
            return job

    def request_stop(self, job_id):
        with self.lock:
            job = self.jobs.get(job_id)
            if not job:
                return None
            job.stop_requested = True
            if job.status in {"started", "running"}:
                job.status = "stopped"
                job.message = "任务已停止"
                job.updated_at = time.time()
                job.events.append({"event": "stopped", "data": self.serialize(job)})
            return job

    def get(self, job_id):
        with self.lock:
            return self.jobs.get(job_id)

    def serialize(self, job):
        data = {
            "jobId": job.id,
            "action": job.action,
            "status": job.status,
            "progress": job.progress,
            "message": job.message,
        }
        if job.result is not None:
            data["result"] = job.result
        if job.error:
            data.update(
                {
                    "error": job.error,
                    "category": job.category or "unknown",
                    "recoverable": job.recoverable,
                    "suggestion": job.suggestion or "查看日志后重试",
                }
            )
        return data

    def events_for(self, job_id):
        job = self.get(job_id)
        if not job:
            return []
        if not job.events:
            return [{"event": "progress", "data": self.serialize(job)}]
        return list(job.events)

    def sse_payload(self, job_id):
        chunks = []
        for item in self.events_for(job_id):
            chunks.append(f"event: {item['event']}\n")
            chunks.append(f"data: {json.dumps(item['data'], ensure_ascii=False)}\n\n")
        return "".join(chunks)

    def _save(self, job):
        with self.lock:
            self.jobs[job.id] = job
