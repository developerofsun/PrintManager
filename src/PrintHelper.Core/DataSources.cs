using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Reflection;

namespace PrintHelper
{
    /// <summary>Registers ADO.NET providers by name. SQL Server is built in; add others from your host app.</summary>
    public static class DbProviders
    {
        static readonly Dictionary<string, DbProviderFactory> Map =
            new Dictionary<string, DbProviderFactory>(StringComparer.OrdinalIgnoreCase)
            {
                { "SqlServer", Microsoft.Data.SqlClient.SqlClientFactory.Instance }
            };

        /// <example>DbProviders.Register("PostgreSQL", Npgsql.NpgsqlFactory.Instance);</example>
        public static void Register(string name, DbProviderFactory factory) { Map[name] = factory; }

        public static DbProviderFactory Get(string name)
        {
            DbProviderFactory f;
            if (!Map.TryGetValue(name ?? "SqlServer", out f))
                throw new ReportException("Database provider '" + name + "' is not registered. Call DbProviders.Register first.");
            return f;
        }
    }

    public class ParamDef
    {
        public string Name { get; set; } = "";
        public string Value { get; set; } = "";
        public override string ToString() { return Name + "=" + Value; }
    }

    /// <summary>A named SQL query. Its result becomes a table the report can bind to.</summary>
    public class DataSourceDef
    {
        public string Name { get; set; } = "DataSource1";
        public string Provider { get; set; } = "SqlServer";
        public string ConnectionString { get; set; } = "";
        public string Query { get; set; } = "";
        /// <summary>Query parameters (@Name). A variable set on the engine with the same name overrides Value.</summary>
        public List<ParamDef> Parameters { get; set; } = new List<ParamDef>();

        public override string ToString() { return Name; }

        DbCommand Prepare(DbConnection conn, IDictionary<string, object> vars, bool schemaOnly)
        {
            var cmd = conn.CreateCommand();
            cmd.CommandText = Query;
            cmd.CommandTimeout = 60;
            foreach (var p in Parameters)
            {
                if (string.IsNullOrWhiteSpace(p.Name)) continue;
                var dp = cmd.CreateParameter();
                dp.ParameterName = "@" + p.Name.TrimStart('@');
                object v;
                if (schemaOnly) dp.Value = DBNull.Value;
                else if (vars != null && vars.TryGetValue(p.Name.TrimStart('@'), out v)) dp.Value = v ?? DBNull.Value;
                else dp.Value = p.Value ?? "";
                cmd.Parameters.Add(dp);
            }
            return cmd;
        }

        public DataTable Execute(IDictionary<string, object> vars)
        {
            try
            {
                var factory = DbProviders.Get(Provider);
                using (var conn = factory.CreateConnection())
                {
                    conn.ConnectionString = ConnectionString;
                    conn.Open();
                    using (var cmd = Prepare(conn, vars, false))
                    using (var da = factory.CreateDataAdapter())
                    {
                        da.SelectCommand = cmd;
                        var dt = new DataTable(Name);
                        da.Fill(dt);
                        return dt;
                    }
                }
            }
            catch (ReportException) { throw; }
            catch (Exception ex) { throw new ReportException("Data source '" + Name + "' failed: " + ex.Message, ex); }
        }

        /// <summary>Column list only (no rows) - used by the designer's field tree and "Test".</summary>
        public DataTable GetSchema(IDictionary<string, object> vars)
        {
            var factory = DbProviders.Get(Provider);
            using (var conn = factory.CreateConnection())
            {
                conn.ConnectionString = ConnectionString;
                conn.Open();
                using (var cmd = Prepare(conn, vars, true))
                using (var da = factory.CreateDataAdapter())
                {
                    da.SelectCommand = cmd;
                    var dt = new DataTable(Name);
                    da.FillSchema(dt, SchemaType.Source);
                    return dt;
                }
            }
        }
    }

    /// <summary>Holds the tables a report runs against, whether from SQL or supplied by the host app.</summary>
    public class DataRegistry
    {
        readonly Dictionary<string, DataTable> _tables = new Dictionary<string, DataTable>(StringComparer.OrdinalIgnoreCase);

        public IEnumerable<string> Names { get { return _tables.Keys.ToList(); } }
        public bool Contains(string name) { return _tables.ContainsKey(name); }
        public void Remove(string name) { _tables.Remove(name); }

        public DataTable Get(string name)
        {
            DataTable t;
            return name != null && _tables.TryGetValue(name, out t) ? t : null;
        }

        /// <summary>Accepts DataTable, DataView, DataSet (first table), or any IEnumerable of objects (List&lt;T&gt;, arrays, EF results...).</summary>
        public void Add(string name, object data)
        {
            if (data == null) throw new ArgumentNullException("data");
            var dt = data as DataTable;
            if (dt == null)
            {
                var dv = data as DataView;
                var ds = data as DataSet;
                var en = data as IEnumerable;
                if (dv != null) dt = dv.ToTable();
                else if (ds != null && ds.Tables.Count > 0) dt = ds.Tables[0];
                else if (en != null) dt = FromEnumerable(en);
                else throw new ReportException("Unsupported data type " + data.GetType().Name + ".");
            }
            if (!string.Equals(dt.TableName, name, StringComparison.OrdinalIgnoreCase))
            {
                dt = dt.Copy();
                dt.TableName = name;
            }
            _tables[name] = dt;
        }

        static DataTable FromEnumerable(IEnumerable list)
        {
            Type et = null;
            foreach (var i in list.GetType().GetInterfaces())
                if (i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>)) { et = i.GetGenericArguments()[0]; break; }
            if (et == null || et == typeof(object))
                foreach (var o in list) if (o != null) { et = o.GetType(); break; }

            var dt = new DataTable();
            if (et == null) return dt;

            var props = et.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                          .Where(p => p.CanRead && p.GetIndexParameters().Length == 0).ToArray();
            foreach (var p in props)
                dt.Columns.Add(p.Name, Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType);
            foreach (var o in list)
            {
                var row = dt.NewRow();
                for (int i = 0; i < props.Length; i++) row[i] = props[i].GetValue(o, null) ?? DBNull.Value;
                dt.Rows.Add(row);
            }
            return dt;
        }
    }
}
