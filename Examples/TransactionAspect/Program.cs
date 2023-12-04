using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
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
			[PrimaryKey, Identity] public int    CustomerID  = default;
			[Column, NotNull]      public string CompanyName = default!;
		}

		static readonly DataOptions _options = new DataOptions().UseSQLiteMicrosoft("Data Source=TestDatabase.sqlite");

		static void Main()
		{
			using var db = new DataConnection(_options);

			db
				.CreateTable<Customer>(tableOptions : TableOptions.CheckExistence)
				.BulkCopy(
					new Customer[]
					{
						new() { CompanyName = "Company 1" },
						new() { CompanyName = "Company 2" }
					});

			PrintList(GetCustomers(db));
			PrintList(GetCustomersAsync(db).Result);

			static void PrintList(List<Customer> list)
			{
				foreach (var customer in list)
				{
					Console.WriteLine($"{customer.CustomerID} : {customer.CompanyName}");
				}
			}
		}

		[Transaction(IsolationLevel = IsolationLevel.ReadUncommitted)]
		public static List<Customer> GetCustomers(DataConnection db)
		{
			return db.GetTable<Customer>().ToList();
		}

		[Transaction]
		public static Task<List<Customer>> GetCustomersAsync(DataConnection db)
		{
			return db.GetTable<Customer>().ToListAsync();
		}
	}
}
