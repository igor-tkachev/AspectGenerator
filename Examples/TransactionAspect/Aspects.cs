using System;
using System.Data;
using System.Threading.Tasks;

using AspectGenerator;

using LinqToDB.Data;

namespace Aspects
{
	/// <summary>
	/// Transaction aspect (linq2db version).
	/// </summary>
	[Aspect(
		PassArguments     = true,
		OnBeforeCall      = nameof(OnBeforeCall),
		OnBeforeCallAsync = nameof(OnBeforeCallAsync),
		OnFinally         = nameof(OnFinally),
		OnFinallyAsync    = nameof(OnFinallyAsync)
		)]
	[AttributeUsage(AttributeTargets.Method, Inherited = false)]
	sealed class TransactionAttribute : Attribute
	{
		public IsolationLevel IsolationLevel { get; set; }

		public static void OnBeforeCall(InterceptInfo info)
		{
			foreach (var arg in info.MethodArguments!)
			{
				if (arg is DataConnection { Transaction : null } con)
				{
					info.Tag = con;

					if (info.AspectArguments.TryGetValue(nameof(IsolationLevel), out var il))
						con.BeginTransaction((IsolationLevel)il!);
					else
						con.BeginTransaction();

					break;
				}
			}
		}

		public static Task OnBeforeCallAsync(InterceptInfo info)
		{
			foreach (var arg in info.MethodArguments!)
			{
				if (arg is DataConnection { Transaction : null } con)
				{
					info.Tag = con;

					return info.AspectArguments.TryGetValue(nameof(IsolationLevel), out var il)
						? con.BeginTransactionAsync((IsolationLevel)il!)
						: con.BeginTransactionAsync();
				}
			}

			return Task.CompletedTask;
		}

		public static void OnFinally(InterceptInfo info)
		{
			if (info.Tag is DataConnection con)
			{
				if (info.Exception is null)
					con.RollbackTransaction();
				else
					con.CommitTransaction();
			}
		}

		public static Task OnFinallyAsync(InterceptInfo info)
		{
			if (info.Tag is DataConnection con)
			{
				return info.Exception is null
					? con.RollbackTransactionAsync()
					: con.CommitTransactionAsync();
			}

			return Task.CompletedTask;
		}
	}
}
