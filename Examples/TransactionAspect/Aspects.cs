using System;
using System.Data;

using AspectGenerator;

using LinqToDB.Data;

namespace Aspects
{
	/// <summary>
	/// Transaction aspect.
	/// </summary>
	[Aspect(
		PassArguments = true,
		OnBeforeCall  = nameof(OnBeforeCall),
		OnFinally     = nameof(OnFinally)
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
	}
}
