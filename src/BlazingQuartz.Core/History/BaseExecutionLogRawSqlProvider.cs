using System;

namespace BlazingQuartz.Core.History
{
    public class BaseExecutionLogRawSqlProvider : IExecutionLogRawSqlProvider
    {
        public virtual string DeleteLogsByDays { get; } =
            @"DELETE FROM bili_execution_logs
WHERE DateAddedUtc < {0}";
    }
}
