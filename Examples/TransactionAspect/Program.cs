using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;

using Aspects;

using LinqToDB;
using LinqToDB.Data;
using LinqToDB.Mapping;

namespace TransactionAspect
{
	static class Program
	{
		[Table(Name="Customers")]
		public sealed class Customer
		{
			[PrimaryKey]      public string CustomerID  = null!;
			[Column, NotNull] public string CompanyName = null!;
		}

		static readonly DataOptions _options = new DataOptions().UseSQLiteMicrosoft("Data Source=TestDatabase");

		static void Main()
		{
			using var db = new DataConnection(_options);

			db.CreateTable<Customer>(tableOptions : TableOptions.CheckExistence);

			var list = GetCustomers(db);
		}

		[Transaction(IsolationLevel = IsolationLevel.ReadUncommitted)]
		public static List<Customer> GetCustomers(DataConnection db)
		{
			return db.GetTable<Customer>().ToList();
		}
	}
}
