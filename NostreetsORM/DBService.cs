﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Web;
using System.ComponentModel.DataAnnotations.Schema;
using NostreetsExtensions.Helpers;
using NostreetsExtensions.Interfaces;
using NostreetsExtensions;

namespace NostreetsORM
{
    public class DBService<T> : SqlService, IDBService<T>
    {
        public DBService() : base()
        {
            try
            {
                SetUp();

                if (!CheckIfTableExist(_type))
                {
                    CreateTable(_type);
                }
                else if (!CheckIfTypeIsCurrent(_type))
                {
                    UpdateTable(_type);
                }
            }
            catch (Exception ex)
            {

                throw ex;
            }
        }

        public DBService(string connectionKey) : base(connectionKey)
        {

            try
            {
                SetUp();
                bool doesExist = CheckIfTableExist(_type),
                     isCurrent = TablesAccessed.All(a => CheckIfTypeIsCurrent(a));

                if (!doesExist)
                {
                    CreateTable(_type);
                }
                else if (!isCurrent)
                {
                    Type[] typesToUpdate = TablesAccessed.Where(a => !CheckIfTypeIsCurrent(a)).ToArray();

                    foreach (Type tbl in typesToUpdate)
                    {
                        UpdateTable(tbl);
                    }
                }
            }
            catch (Exception ex)
            {

                throw ex;
            }
        }

        public Type[] TablesAccessed { get { return GetTablesAccessed(); } }

        private bool _tableCreation = false;
        private bool _procedureCreation = false;
        private Type _type = typeof(T);
        private Dictionary<string, string> _partialProcs = new Dictionary<string, string>();


        #region Internal Logic

        private void SetUp()
        {
            _partialProcs.Add("GetAllColumns", "SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = '{0}s'");
            _partialProcs.Add("GetAllProcs", "SELECT NAME FROM dbo.sysobjects WHERE(type = 'P')");
            _partialProcs.Add("CheckIfTableExist", "Declare @IsTrue int = 0 IF EXISTS(SELECT * FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = N'{0}s') Begin Set @IsTrue = 1 End Select @IsTrue");
            _partialProcs.Add("InsertProcedure", "CREATE Proc [dbo].[{0}s_Insert] {1} As Begin Declare @NewId {2} Insert Into dbo.{3}s({4}) Values({5}) Set @NewId = SCOPE_IDENTITY() Select @NewId End");
            _partialProcs.Add("UpdateProcedure", "CREATE Proc [dbo].[{0}s_Update] {1} As Begin {2} End");
            _partialProcs.Add("DeleteProcedure", "CREATE Proc [dbo].[{0}s_Delete] @{1} {2} As Begin Delete {0}s Where {1} = @{1} {3} End");
            _partialProcs.Add("SelectProcedure", "CREATE Proc [dbo].[{0}s_Select{5}] {3} AS Begin SELECT {1} FROM [dbo].[{0}s] {2} {4} End");
            _partialProcs.Add("NullCheckForUpdatePartial", "If @{2} Is Not Null Begin Update dbo.{0} s {1} End ");
            _partialProcs.Add("CreateTableType", "CREATE TYPE [dbo].[{0}] AS TABLE( {1} )");
            _partialProcs.Add("CreateTable", "Declare @isTrue int = 0 Begin CREATE TABLE [dbo].[{0}s] ( {1} ); IF EXISTS(SELECT* FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = N'{0}s') Begin Set @IsTrue = 1 End End Select @IsTrue");
            _partialProcs.Add("CreateColumn", "[{0}] {1} {2}");


            _partialProcs.Add("SelectStatement", " SELECT {0}");
            _partialProcs.Add("FromStatement", " FROM [dbo].[{0}s]");
            _partialProcs.Add("InsertStatement", " INSERT INTO dbo.{0}s({1})");
            _partialProcs.Add("ValuesStatement", " Values({2})");
            _partialProcs.Add("CopyTableStatement", "SELECT {2} INTO {1}s FROM {0}s");
            _partialProcs.Add("IfStatement", " IF {0} BEGIN {1} END");
            _partialProcs.Add("ElseStatement", " ELSE BEGIN {0} END");
            _partialProcs.Add("ElseIfStatement", " ELSE IF BEGIN {0} END");
            _partialProcs.Add("DeclareStatement", " DECLARE {0} {1} = {2}");
            _partialProcs.Add("DeleteRowsStatement", " DELETE {0}s");
            _partialProcs.Add("DropTableStatement", " DROP TABLE {0}s");
            _partialProcs.Add("DropProcedureStatement", " DROP PROCEDURE {0}");
            _partialProcs.Add("WhereStatement", " WHERE {0} BEGIN {1} END");
            _partialProcs.Add("CountStatement", " COUNT({0})");
            _partialProcs.Add("GroupByStatement", " GROUP BY {0}");
            _partialProcs.Add("PrimaryKeyStatement", "PRIMARY KEY CLUSTERED ([{0}] ASC)");
        }

        private bool ShouldNormalize(Type type)
        {
            return ((type.BaseType == typeof(Enum) || type.IsClass) && (type != typeof(String) && type != typeof(Char))) ? true : false;
        }

        private string DeterminSQLType(Type type)
        {
            string statement = null;
            if ((type.BaseType.Name == nameof(Enum) || type.IsClass) && (type.Name != nameof(String) && type.Name != nameof(Char)))
            {
                statement = "INT";
            }
            else
            {
                switch (type.Name)
                {
                    case nameof(String):
                        statement = "NVARCHAR (2000)";
                        break;

                    case nameof(Int16):
                        statement = "SMALLINT";
                        break;

                    case nameof(Int32):
                        statement = "INT";
                        break;

                    case nameof(Boolean):
                        statement = "BIT";
                        break;

                    case nameof(DateTime):
                        statement = "DATETIME2 (7)" + ((_tableCreation) ? "CONSTRAINT[DF_" + type.DeclaringType.Name + "s_" + type.Name + "] DEFAULT(getutcdate())" : "");
                        break;

                    default:
                        statement = "NVARCHAR (MAX)";
                        break;
                }
            }

            return statement;
        }

        private Type[] GetTablesAccessed()
        {
            List<Type> result = new List<Type>()
            {
                _type
            };

            PropertyInfo[] types = _type.GetProperties().Where(a => ShouldNormalize(a.PropertyType)).ToArray();

            foreach (PropertyInfo prop in types)
            {
                if (!result.Contains(prop.PropertyType))
                {
                    result.Add(prop.PropertyType); 
                }
            }
            return result.ToArray();
        }

        private void UpdateTable(Type type)
        {
            foreach (Type table in TablesAccessed)
            {
                CreateBackupTable(type);

                DropTable(table);

                CreateTable(type);

                UpdateRows(type);

                DropBackupTable(type);
            }
        }
        #endregion

        #region Queries 

        private List<string> GetAllColumns(Type type)
        {
            string query = _partialProcs["GetAllColumns"].FormatString(type.Name);
            List<string> list = null;

            DataProvider.ExecuteCmd(() => Connection,
               query,
                null,
                (reader, set) =>
                {
                    string column = DataMapper<string>.Instance.MapToObject(reader);
                    if (list == null) { list = new List<string>(); }
                    list.Add(column);
                },
                null, mod => mod.CommandType = CommandType.Text);


            return list;

        }

        private List<string> GetAllProcs(Type type)
        {
            string query = _partialProcs["GetAllProcs"];
            List<string> list = null;

            DataProvider.ExecuteCmd(() => Connection,
               query,
                null,
                (reader, set) =>
                {
                    string proc = DataMapper<string>.Instance.MapToObject(reader);
                    if (list == null) { list = new List<string>(); }
                    list.Add(proc);
                },
                null, mod => mod.CommandType = CommandType.Text);


            List<string> result = list.Where(a => a.Contains(type.Name)).ToList();

            return result;
        }

        private void DropBackupTable(Type type)
        {
            string sqlTemp = _partialProcs["DropTableStatement"];
            string query = String.Format(sqlTemp, "temp" + type.Name);
            object result = null;

            DataProvider.ExecuteCmd(() => Connection,
               query,
                null,
                (reader, set) =>
                {
                    result = DataMapper<object>.Instance.MapToObject(reader);
                },
                null, mod => mod.CommandType = CommandType.Text);
        }

        private void DropProcedures(Type type)
        {
            List<string> classProcs = GetAllProcs(type);

            foreach (string proc in classProcs)
            {
                string sqlTemp = _partialProcs["DropProcedureStatement"];
                string query = String.Format(sqlTemp, proc);
                object result = null;

                DataProvider.ExecuteCmd(() => Connection,
                   query,
                    null,
                    (reader, set) =>
                    {
                        result = DataMapper<object>.Instance.MapToObject(reader);
                    },
                    null, mod => mod.CommandType = CommandType.Text);
            }


        }

        private void DropTable(Type type)
        {
            string sqlTemp = _partialProcs["DropTableStatement"];
            string query = String.Format(sqlTemp, type.Name + 's');

            object result = null;

            DataProvider.ExecuteCmd(() => Connection,
               query,
                null,
                (reader, set) =>
                {
                    result = DataMapper<object>.Instance.MapToObject(reader);
                },
                null, mod => mod.CommandType = CommandType.Text);

            DropProcedures(_type);
        }

        private bool CheckIfEnumIsCurrent(Type type)
        {
            bool result = false;
            Dictionary<int, string> currentEnums = type.ToDictionary(),
                                    dbEnums = null;

            DataProvider.ExecuteCmd(() => Connection,
                "SELECT * FROM {0}s".FormatString(type.Name), null,
                (reader, set) =>
                {
                    if (dbEnums == null)
                        dbEnums = new Dictionary<int, string>();

                    dbEnums.Add(reader.GetSafeInt32(0), reader.GetSafeString(1));

                }, null, cmd => cmd.CommandType = CommandType.Text);

            if (currentEnums.Count == dbEnums.Count) { result = true; }

            return result;
        }

        private bool CheckIfTableExist(Type type)
        {
            string sqlTemp = _partialProcs["CheckIfTableExist"];
            string query = String.Format(sqlTemp, type.Name);

            int isTrue = 0;
            DataProvider.ExecuteCmd(() => Connection,
               query,
                null,
                (reader, set) =>
                {
                    isTrue = reader.GetSafeInt32(0);
                },
                null, mod => mod.CommandType = CommandType.Text);

            if (isTrue == 1) { return true; }

            return false;
        }

        private bool CheckIfTypeIsCurrent(Type type)
        {
            bool result = true;

            if (!type.IsEnum)
            {
                List<PropertyInfo> baseProps = type.GetProperties().ToList();
                List<string> columnsInTable = DataProvider.GetSchema(() => Connection, type.Name + 's');
                Func<PropertyInfo, bool> predicate = (a) =>
                {
                    bool _result = false;
                    _result = columnsInTable.Any(b => b == a.Name);


                    if (a.PropertyType.BaseType == typeof(Enum))
                        _result = CheckIfEnumIsCurrent(a.PropertyType);


                    return _result;
                };


                List<PropertyInfo> excludedProps = baseProps.GetPropertiesByAttribute<NotMappedAttribute>(type);
                List<PropertyInfo> includedProps = (excludedProps.Count > 0) ? baseProps.Where(a => excludedProps.Any(b => b.Name != a.Name)).ToList() : baseProps;
                List<PropertyInfo> matchingProps = includedProps.Where(predicate).ToList();

                if (matchingProps.Count != includedProps.Count)
                {
                    result = false;
                }

            }
            else
            {
                result = CheckIfEnumIsCurrent(type);
            }

            return result;

        }

        private void CreateBackupTable(Type type)
        {
            string sqlTemp = _partialProcs["CopyTableStatement"];
            string query = String.Format(sqlTemp, type.Name, "temp" + type.Name, "*");
            object result = null;

            DataProvider.ExecuteCmd(() => Connection,
               query,
                null,
                (reader, set) =>
                {
                    result = DataMapper<object>.Instance.MapToObject(reader);
                },
                null, mod => mod.CommandType = CommandType.Text);

        }

        private void CreateProcedures(Type type)
        {
            _procedureCreation = true;

            string sqlInsertTemp = _partialProcs["InsertProcedure"],
                   sqlUpdateTemp = _partialProcs["UpdateProcedure"],
                   sqlSelectTemp = _partialProcs["SelectProcedure"],
                   sqlDeleteTemp = _partialProcs["DeleteProcedure"],
                   sqlUpdateNullCheckTemp = _partialProcs["NullCheckForUpdatePartial"];


            string[] temps = { sqlInsertTemp, sqlSelectTemp, sqlSelectTemp, sqlUpdateTemp, sqlDeleteTemp };

            for (int x = 0; x < temps.Length; x++)
            {

                string query = null;

                string inputParams = null,
                       columns = null,
                       values = null,
                       select = null,
                       joins = null,
                       deletes = null,
                       updatesParams = null;

                List<string> inputs = new List<string>(),
                             colm = new List<string>(),
                             val = new List<string>(),
                             sel = new List<string>(),
                             jns = new List<string>(),
                             dels = new List<string>(),
                             updtParams = new List<string>(),
                             innerUpdt = new List<string>();

                if (type.IsClass && (type != typeof(String) || type != typeof(Char)))
                {
                    PropertyInfo[] props = type.GetProperties();

                    for (int i = 0; i < props.Length; i++)
                    {
                        string PK = (props[i].PropertyType.BaseType == typeof(Enum) ? "Id" : props[i].Name);

                        if (i > 0)
                        {
                            inputs.Add("@" + props[i].Name + " " + DeterminSQLType(props[i].PropertyType) + (i == props.Length - 1 ? "" : ","));

                            colm.Add(props[i].Name +
                                (/*(props[i].PropertyType.BaseType.Name == nameof(Enum) || props[i].PropertyType.IsClass) && (props[i].PropertyType.Name != nameof(String) && props[i].PropertyType.Name != nameof(Char))*/
                                ShouldNormalize(props[i].PropertyType) ? "Id" : "") + (i == props.Length - 1 ? "" : ","));

                            val.Add("@" + props[i].Name + (i == props.Length - 1 ? "" : ","));
                        }

                        updtParams.Add("@" + props[i].Name + DeterminSQLType(props[i].PropertyType) + (i == 0 ? "" : " = NULL") + (i == props.Length - 1 ? "" : ","));

                        innerUpdt.Add("SET " + props[i].Name +
                            (/*(props[i].PropertyType.BaseType.Name == nameof(Enum) || props[i].PropertyType.IsClass) && (props[i].PropertyType.Name != nameof(String) && props[i].PropertyType.Name != nameof(Char))*/
                            ShouldNormalize(props[i].PropertyType) ? "Id" : "") + " = @" + props[i].Name + " WHERE " + type.Name + "s." + props[0].Name + " = @" + props[0].Name);


                        if (/*(props[i].PropertyType.BaseType.Name == nameof(Enum) || props[i].PropertyType.IsClass) && (props[i].PropertyType.Name != nameof(String) && props[i].PropertyType.Name != nameof(Char))*/ShouldNormalize(props[i].PropertyType))
                        {
                            jns.Add("Inner Join " + props[i].PropertyType.Name + "s AS " + props[i].Name + "Id On " + props[i].Name + "Id." + (props[i].PropertyType.BaseType == typeof(Enum) ? "Id" : props[i].Name + "Id") + " = " + type.Name + "s." + props[i].Name + "Id");

                            if (!props[i].PropertyType.Namespace.Contains("System") && props[i].PropertyType.BaseType != typeof(Enum))
                            {
                                dels.Add("Delete " + props[i].Name + "s Where " + PK + " = (Select " + PK + " From " + type.Name + " Where " + PK + " = @" + PK + ")");
                            }
                        }

                        sel.Add(type.Name + "s.[" + props[i].Name + (/*(props[i].PropertyType.BaseType.Name == nameof(Enum) || props[i].PropertyType.IsClass) && (props[i].PropertyType.Name != nameof(String) && props[i].PropertyType.Name != nameof(Char))*/
                            ShouldNormalize(props[i].PropertyType) ? "Id" : "") + "]" + (i == props.Length - 1 ? " " : ","));
                    }

                    inputParams = String.Join(" ", inputs.ToArray());
                    columns = String.Join(" ", colm.ToArray());
                    values = String.Join(" ", val.ToArray());
                    select = String.Join(" ", sel.ToArray());
                    joins = String.Join(" ", jns.ToArray());
                    deletes = String.Join(" ", dels.ToArray());
                    updatesParams = String.Join(" ", updtParams.ToArray());

                    if (temps[x] == sqlInsertTemp)
                    {
                        query = String.Format(temps[x], type.Name, inputParams, DeterminSQLType(props[0].PropertyType), type.Name, columns, values);
                    }
                    else if (temps[x] == sqlSelectTemp)
                    {
                        if (x == 1)
                        {
                            query = String.Format(temps[x], type.Name, select, joins, "", "", "All");
                        }
                        else
                        {
                            query = String.Format(temps[x], type.Name, select, joins, "@" + props[0].Name + " " + DeterminSQLType(props[0].PropertyType/*, type.Name*/), "Where " + type.Name + "s." + props[0].Name + " = @" + props[0].Name, "ById");
                        }
                    }
                    else if (temps[x] == sqlUpdateTemp)
                    {

                        string innerQuery = null;
                        for (int q = 1; q < innerUpdt.Count; q++)
                        {
                            innerQuery += String.Format(sqlUpdateNullCheckTemp, type.Name, innerUpdt[q], props[q].Name);
                        }

                        query = String.Format(temps[x], type.Name, "@Id INT, " + inputParams, innerQuery);
                    }
                    else if (temps[x] == sqlDeleteTemp)
                    {

                        query = String.Format(temps[x], type.Name, props[0].Name, DeterminSQLType(props[0].PropertyType/*, type.Name*/), deletes);
                    }


                }
                else if (type.BaseType == typeof(Enum))
                {

                    inputs.Add("@Value " + DeterminSQLType(typeof(string)));
                    updtParams.Add("@Id " + DeterminSQLType(typeof(int)) + ", @Value " + DeterminSQLType(typeof(string)) + " = Null");
                    innerUpdt.Add("SET Value = @Value WHERE " + type.Name + "s.Id = @Id");
                    colm.Add("Value");
                    val.Add("@Value");
                    sel.Add(type.Name + "s.[Id], " + type.Name + "s.[Value]");


                    inputParams = String.Join(" ", inputs.ToArray());
                    columns = String.Join(" ", colm.ToArray());
                    values = String.Join(" ", val.ToArray());
                    select = String.Join(" ", sel.ToArray());
                    joins = String.Join(" ", jns.ToArray());
                    deletes = String.Join(" ", dels.ToArray());
                    updatesParams = String.Join(" ", updtParams.ToArray());

                    if (temps[x] == sqlInsertTemp)
                    {
                        query = String.Format(temps[x], type.Name, inputParams, DeterminSQLType(typeof(int)/*, type.Name*/), type.Name, columns, values);
                    }
                    else if (temps[x] == sqlSelectTemp)
                    {
                        if (x == 1)
                        {
                            query = String.Format(temps[x], type.Name, select, "", "", "", "All");
                        }
                        else
                        {
                            query = String.Format(temps[x], type.Name, select, "", "@Id " + DeterminSQLType(typeof(int)/*, type.Name*/), "Where " + type.Name + "s.Id = @Id", "ById");
                        }
                    }
                    else if (temps[x] == sqlUpdateTemp)
                    {

                        string innerQuery = null;
                        for (int num = 0; num < innerUpdt.Count; num++)
                        {

                            innerQuery += String.Format(sqlUpdateNullCheckTemp, type.Name, innerUpdt[num], "Value");
                        }

                        query = String.Format(temps[x], type.Name, " @Id INT, " + inputParams, innerQuery);
                    }
                    else if (temps[x] == sqlDeleteTemp)
                    {

                        query = String.Format(temps[x], type.Name, "Id", DeterminSQLType(typeof(int)/*, type.Name*/), deletes);
                    }
                }


                DataProvider.ExecuteCmd(() => Connection,
                   query,
                    null,
                    (reader, set) =>
                    {
                        object id = DataMapper<object>.Instance.MapToObject(reader);
                    },
                    null, mod => mod.CommandType = System.Data.CommandType.Text);
            }

            _procedureCreation = false;

        }

        private Dictionary<string, string> CreateTable(Type type)
        {
            _tableCreation = true;

            Dictionary<string, string> result = null;
            List<string> columns = new List<string>();
            List<string> FKs = new List<string>();

            if (CheckIfTableExist(type))
            {
                result = new Dictionary<string, string>();
                result.Add("Name", type.Name + "s");

                if (type.IsClass && (type != typeof(String) || type != typeof(Char)))
                {
                    result.Add("PK", type.GetProperties()[0].Name);
                }
                else if (type.BaseType == typeof(Enum))
                {
                    result.Add("PK", "Id");
                }
                return result;
            }
            else if (type.IsClass && (type != typeof(String) || type != typeof(Char)))
            {
                PropertyInfo[] props = type.GetProperties();
                string endingTable = "NOT NULL, CONSTRAINT [PK_" + type.Name + "s] PRIMARY KEY CLUSTERED ([" + props[0].Name + "] ASC)";


                foreach (var item in props)
                {
                    string FK = null;

                    columns.Add(
                        String.Format(
                            _partialProcs["CreateColumn"],
                           /* ((item.PropertyType.BaseType.Name == nameof(Enum) || item.PropertyType.IsClass) && (item.PropertyType.Name != nameof(String) && item.PropertyType.Name != nameof(Char)))*/ ShouldNormalize(item.PropertyType) ? item.Name + "Id" : item.Name,
                            DeterminSQLType(item.PropertyType),
                            props[0] == item ? "IDENTITY (1, 1) NOT NULL, " : props[props.Length - 1] == item ? endingTable + "," + String.Join(", ", FKs.ToArray()) : "NOT NULL, ")
                        );


                    if (/*item.PropertyType.IsClass && (item.PropertyType.Name != nameof(String) && item.PropertyType.Name != nameof(Char)) || item.PropertyType.BaseType.Name == nameof(Enum)*/ShouldNormalize(item.PropertyType))
                    {
                        Dictionary<string, string> normalizedTbl = CreateTable(item.PropertyType);
                        FK = "CONSTRAINT [FK_" + type.Name + "s_" + item.Name + "] FOREIGN KEY ([" + item.Name + "Id]) REFERENCES [dbo].[" + normalizedTbl["Name"] + "] ([" + normalizedTbl["PK"] + "])";
                        FKs.Add(FK);
                    }
                }
            }
            else if (type.BaseType == typeof(Enum))
            {
                string endingTable = "NOT NULL, CONSTRAINT [PK_" + type.Name + "s] PRIMARY KEY CLUSTERED ([Id] ASC)";
                for (int i = 0; i < 2; i++)
                {
                    columns.Add(
                        String.Format(
                            _partialProcs["CreateColumn"],
                            i == 0 ? "Id" : "Value",
                            DeterminSQLType(i == 0 ? typeof(int) : typeof(string)),
                            i == 0 ? "IDENTITY (1, 1) NOT NULL, " : endingTable)
                     );
                }
            }


            string table = String.Concat(columns.ToArray());
            string query = String.Format(_partialProcs["CreateTable"], type.Name, table);



            int isTrue = 0;
            DataProvider.ExecuteCmd(() => Connection,
               query,
                null,
                (reader, set) =>
                {
                    isTrue = reader.GetSafeInt32(0);
                },
                null,
                mod => mod.CommandType = System.Data.CommandType.Text);


            if (isTrue == 1)
            {
                CreateProcedures(type);
                result = new Dictionary<string, string>();
                result.Add("Name", type.Name + "s");

                if (type.IsClass && (type != typeof(String) || type != typeof(Char)))
                {
                    result.Add("PK", type.GetProperties()[0].Name);
                }
                else if (type.BaseType == typeof(Enum))
                {
                    var fields = type.GetFields();
                    result.Add("PK", "Id");

                    for (int i = 1; i < fields.Length; i++)
                    {
                        DataProvider.ExecuteNonQuery(() => Connection,
                                        "dbo." + type.Name + "s_Insert",
                                        (param) => param.Add(new SqlParameter("Value", fields[i].Name)),
                                        null);
                    }
                }
            }


            _tableCreation = false;
            return result;
        }

        private void UpdateRows(Type type)
        {
            object result = null;
            PropertyInfo[] props = type.GetProperties();
            List<string> columns = new List<string>();

            for (int i = 0; i < props.Length; i++)
            {
                if (i > 0)
                {
                    columns.Add(props[i].Name + (ShouldNormalize(props[i].PropertyType) ? "Id" : "") + (i == props.Length - 1 ? "" : ","));
                }
            }

            string inputs = String.Join(" ", columns.ToArray());

            List<string> oldColumns = GetAllColumns(type);
            string query = _partialProcs["InsertStatement"].FormatString(type.Name, inputs);
            query += _partialProcs["SelectStatement"].FormatString("*") + _partialProcs["FromStatement"].FormatString("temp" + type.Name);

            DataProvider.ExecuteCmd(() => Connection,
               query,
                null,
                (reader, set) =>
                {
                    result = DataMapper<object>.Instance.MapToObject(reader);
                },
                null, mod => mod.CommandType = CommandType.Text);

        }

        #endregion

        public List<T> GetAll()
        {
            List<T> list = null;
            DataProvider.ExecuteCmd(() => Connection, "dbo." + _type.Name + "s_SelectAll",
                null,
                (reader, set) =>
                {
                    T chart = DataMapper<T>.Instance.MapToObject(reader);
                    if (list == null) { list = new List<T>(); }
                    list.Add(chart);
                });
            return list;
        }

        public void Delete(object id)
        {
            DataProvider.ExecuteNonQuery(() => Connection, "dbo." + _type.Name + "s_Delete",
                param => param.Add(new SqlParameter(typeof(T).GetProperties()[0].Name, typeof(T).GetProperties()[0].GetValue(id))));
        }

        public T Get(object id)
        {
            T chart = default(T);
            DataProvider.ExecuteCmd(() => Connection, "dbo." + _type.Name + "s_SelectById",
                param => param.Add(new SqlParameter(typeof(T).GetProperties()[0].Name, typeof(T).GetProperties()[0].GetValue(id))),
                (reader, set) =>
                {
                    chart = DataMapper<T>.Instance.MapToObject(reader);
                });
            return chart;
        }

        public object Insert(T model)
        {
            object id = 0;
            DataProvider.ExecuteCmd(() => Connection, "dbo." + _type.Name + "s_Insert",
                       param =>
                       {
                           foreach (var prop in typeof(T).GetProperties())
                           {

                               if (prop.Name != "Id")
                               {
                                   if (prop.PropertyType.BaseType.Name == nameof(Enum))
                                   {
                                       param.Add(new SqlParameter(prop.Name, (int)prop.GetValue(model)));

                                   }
                                   else
                                   {
                                       param.Add(new SqlParameter(prop.Name, prop.GetValue(model)));
                                   }
                               }
                           }
                       },
                      (reader, set) =>
                      {
                          id = DataMapper<object>.Instance.MapToObject(reader);
                      });
            return id;
        }

        public void Update(T model)
        {
            DataProvider.ExecuteNonQuery(() => Connection, "dbo." + _type.Name + "s_Update",
                       param =>
                       {
                           param.Add(new SqlParameter(typeof(T).GetProperties()[0].Name, typeof(T).GetProperties()[0].GetValue(model)));
                           foreach (var prop in typeof(T).GetProperties())
                           {
                               param.Add(new SqlParameter(prop.Name, prop.GetValue(model)));
                           }
                       }
                       );
        }

    }

    public class DBService<T, IdType> : SqlService, IDBService<T, IdType>
    {
       public DBService() : base()
        {
            try
            {
                SetUp();

                if (!CheckIfTableExist(_type))
                {
                    CreateTable(_type);
                }
                else if (!CheckIfTypeIsCurrent(_type))
                {
                    UpdateTable(_type);
                }
            }
            catch (Exception ex)
            {

                throw ex;
            }
        }

        public DBService(string connectionKey) : base(connectionKey)
        {

            try
            {
                SetUp();

                if (!CheckIfTableExist(_type))
                {
                    CreateTable(_type);
                }
                else if (!CheckIfTypeIsCurrent(_type))
                {
                    UpdateTable(_type);
                }
            }
            catch (Exception ex)
            {

                throw ex;
            }
        }

        public Type[] TablesAccessed { get { return GetTablesAccessed(); } }

        private bool _tableCreation = false;
        private bool _procedureCreation = false;
        private Type _type = typeof(T);
        private Dictionary<string, string> _partialProcs = new Dictionary<string, string>();


        #region Internal Logic

        private void SetUp()
        {
            _partialProcs.Add("GetAllColumns", "SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = '{0}s'");
            _partialProcs.Add("GetAllProcs", "SELECT NAME FROM dbo.sysobjects WHERE(type = 'P')");
            _partialProcs.Add("CheckIfTableExist", "Declare @IsTrue int = 0 IF EXISTS(SELECT * FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = N'{0}s') Begin Set @IsTrue = 1 End Select @IsTrue");
            _partialProcs.Add("InsertProcedure", "CREATE Proc [dbo].[{0}s_Insert] {1} As Begin Declare @NewId {2} Insert Into dbo.{3}s({4}) Values({5}) Set @NewId = SCOPE_IDENTITY() Select @NewId End");
            _partialProcs.Add("UpdateProcedure", "CREATE Proc [dbo].[{0}s_Update] {1} As Begin {2} End");
            _partialProcs.Add("DeleteProcedure", "CREATE Proc [dbo].[{0}s_Delete] @{1} {2} As Begin Delete {0}s Where {1} = @{1} {3} End");
            _partialProcs.Add("SelectProcedure", "CREATE Proc [dbo].[{0}s_Select{5}] {3} AS Begin SELECT {1} FROM [dbo].[{0}s] {2} {4} End");
            _partialProcs.Add("NullCheckForUpdatePartial", "If @{2} Is Not Null Begin Update dbo.{0} s {1} End ");
            _partialProcs.Add("CreateTable", "Declare @isTrue int = 0 Begin CREATE TABLE [dbo].[{0}s] ( {1} ); IF EXISTS(SELECT* FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = N'{0}s') Begin Set @IsTrue = 1 End End Select @IsTrue");
            _partialProcs.Add("CreateColumn", "[{0}] {1} {2}");


            _partialProcs.Add("SelectStatement", " SELECT {0}");
            _partialProcs.Add("FromStatement", " FROM [dbo].[{0}s]");
            _partialProcs.Add("InsertStatement", " INSERT INTO dbo.{0}s({1})");
            _partialProcs.Add("ValuesStatement", " Values({2})");
            _partialProcs.Add("CopyTableStatement", "SELECT {2} INTO {1}s FROM {0}s");
            _partialProcs.Add("IfStatement", " IF {0} BEGIN {1} END");
            _partialProcs.Add("ElseStatement", " ELSE BEGIN {0} END");
            _partialProcs.Add("ElseIfStatement", " ELSE IF BEGIN {0} END");
            _partialProcs.Add("DeclareStatement", " DECLARE {0} {1} = {2}");
            _partialProcs.Add("DeleteRowsStatement", " DELETE {0}s");
            _partialProcs.Add("DropTableStatement", " DROP TABLE {0}s");
            _partialProcs.Add("DropProcedureStatement", " DROP PROCEDURE {0}");
            _partialProcs.Add("WhereStatement", " WHERE {0} BEGIN {1} END");
            _partialProcs.Add("CountStatement", " COUNT({0})");
            _partialProcs.Add("GroupByStatement", " GROUP BY {0}");

        }

        private bool ShouldNormalize(Type type)
        {
            return ((type.BaseType == typeof(Enum) || type.IsClass) && (type != typeof(String) && type != typeof(Char))) ? true : false;
        }

        private string DeterminSQLType(Type type)
        {
            string statement = null;
            if ((type.BaseType.Name == nameof(Enum) || type.IsClass) && (type.Name != nameof(String) && type.Name != nameof(Char)))
            {
                statement = "INT";
            }
            else
            {
                switch (type.Name)
                {
                    case nameof(String):
                        statement = "NVARCHAR (2000)";
                        break;

                    case nameof(Int16):
                        statement = "SMALLINT";
                        break;

                    case nameof(Int32):
                        statement = "INT";
                        break;

                    case nameof(Boolean):
                        statement = "BIT";
                        break;

                    case nameof(DateTime):
                        statement = "DATETIME2 (7)" + ((_tableCreation) ? "CONSTRAINT[DF_" + type.DeclaringType.Name + "s_" + type.Name + "] DEFAULT(getutcdate())" : "");
                        break;

                    default:
                        statement = "NVARCHAR (MAX)";
                        break;
                }
            }

            return statement;
        }

        private Type[] GetTablesAccessed()
        {
            List<Type> result = new List<Type>()
            {
                _type
            };
            PropertyInfo[] types = _type.GetProperties().Where(a => ShouldNormalize(a.PropertyType)).ToArray();
            foreach (PropertyInfo prop in types)
            {
                result.Add(prop.PropertyType);
            }
            return result.ToArray();
        }

        private void UpdateTable(Type type)
        {
            foreach (Type table in TablesAccessed)
            {
                CreateBackupTable(type);

                DropTable(table);

                CreateTable(type);

                UpdateRows(type);

                DropBackupTable(type);
            }
        }
        #endregion

        #region Queries 

        private List<string> GetAllColumns(Type type)
        {
            string query = _partialProcs["GetAllColumns"].FormatString(type.Name);
            List<string> list = null;

            DataProvider.ExecuteCmd(() => Connection,
               query,
                null,
                (reader, set) =>
                {
                    string column = DataMapper<string>.Instance.MapToObject(reader);
                    if (list == null) { list = new List<string>(); }
                    list.Add(column);
                },
                null, mod => mod.CommandType = CommandType.Text);


            return list;

        }

        private List<string> GetAllProcs(Type type)
        {
            string query = _partialProcs["GetAllProcs"];
            List<string> list = null;

            DataProvider.ExecuteCmd(() => Connection,
               query,
                null,
                (reader, set) =>
                {
                    string proc = DataMapper<string>.Instance.MapToObject(reader);
                    if (list == null) { list = new List<string>(); }
                    list.Add(proc);
                },
                null, mod => mod.CommandType = CommandType.Text);


            List<string> result = list.Where(a => a.Contains(type.Name)).ToList();

            return result;
        }

        private void DropBackupTable(Type type)
        {
            string sqlTemp = _partialProcs["DropTableStatement"];
            string query = String.Format(sqlTemp, "temp" + type.Name);
            object result = null;

            DataProvider.ExecuteCmd(() => Connection,
               query,
                null,
                (reader, set) =>
                {
                    result = DataMapper<object>.Instance.MapToObject(reader);
                },
                null, mod => mod.CommandType = CommandType.Text);
        }

        private void DropProcedures(Type type)
        {
            List<string> classProcs = GetAllProcs(type);

            foreach (string proc in classProcs)
            {
                string sqlTemp = _partialProcs["DropProcedureStatement"];
                string query = String.Format(sqlTemp, proc);
                object result = null;

                DataProvider.ExecuteCmd(() => Connection,
                   query,
                    null,
                    (reader, set) =>
                    {
                        result = DataMapper<object>.Instance.MapToObject(reader);
                    },
                    null, mod => mod.CommandType = CommandType.Text);
            }


        }

        private void DropTable(Type type)
        {
            string sqlTemp = _partialProcs["DropTableStatement"];
            string query = String.Format(sqlTemp, type.Name + 's');

            object result = null;

            DataProvider.ExecuteCmd(() => Connection,
               query,
                null,
                (reader, set) =>
                {
                    result = DataMapper<object>.Instance.MapToObject(reader);
                },
                null, mod => mod.CommandType = CommandType.Text);

            DropProcedures(_type);
        }

        private bool CheckIfEnumIsCurrent(Type type)
        {
            bool result = false;
            Dictionary<int, string> currentEnums = type.ToDictionary(),
                                    dbEnums = null;

            DataProvider.ExecuteCmd(() => Connection,
                "SELECT * FROM {0}s".FormatString(type.Name), null,
                (reader, set) =>
                {
                    if (dbEnums == null)
                        dbEnums = new Dictionary<int, string>();

                    dbEnums.Add(reader.GetSafeInt32(0), reader.GetSafeString(1));

                }, null, cmd => cmd.CommandType = CommandType.Text);

            if (currentEnums.Count == dbEnums.Count) { result = true; }

            return result;
        }

        private bool CheckIfTableExist(Type type)
        {
            string sqlTemp = _partialProcs["CheckIfTableExist"];
            string query = String.Format(sqlTemp, type.Name);

            int isTrue = 0;
            DataProvider.ExecuteCmd(() => Connection,
               query,
                null,
                (reader, set) =>
                {
                    isTrue = reader.GetSafeInt32(0);
                },
                null, mod => mod.CommandType = CommandType.Text);

            if (isTrue == 1) { return true; }

            return false;
        }

        private bool CheckIfTypeIsCurrent(Type type)
        {
            bool result = true;
            List<PropertyInfo> baseProps = type.GetProperties().ToList();
            List<string> columnsInTable = DataProvider.GetSchema(() => Connection, type.Name + 's');
            Func<PropertyInfo, bool> predicate = (a) =>
            {
                bool _result = false;
                _result = columnsInTable.Any(b => b == a.Name);


                if (a.PropertyType.BaseType == typeof(Enum))
                    _result = CheckIfEnumIsCurrent(a.PropertyType);


                return _result;
            };


            List<PropertyInfo> excludedProps = baseProps.GetPropertiesByAttribute<NotMappedAttribute>(type);
            List<PropertyInfo> includedProps = (excludedProps.Count > 0) ? baseProps.Where(a => excludedProps.Any(b => b.Name != a.Name)).ToList() : baseProps;
            List<PropertyInfo> matchingProps = includedProps.Where(predicate).ToList();

            if (matchingProps.Count != includedProps.Count)
            {
                result = false;
            }

            return result;
        }

        private void CreateBackupTable(Type type)
        {
            string sqlTemp = _partialProcs["CopyTableStatement"];
            string query = String.Format(sqlTemp, type.Name, "temp" + type.Name, "*");
            object result = null;

            DataProvider.ExecuteCmd(() => Connection,
               query,
                null,
                (reader, set) =>
                {
                    result = DataMapper<object>.Instance.MapToObject(reader);
                },
                null, mod => mod.CommandType = CommandType.Text);

        }

        private void CreateProcedures(Type type)
        {
            _procedureCreation = true;

            string sqlInsertTemp = _partialProcs["InsertProcedure"],
                   sqlUpdateTemp = _partialProcs["UpdateProcedure"],
                   sqlSelectTemp = _partialProcs["SelectProcedure"],
                   sqlDeleteTemp = _partialProcs["DeleteProcedure"],
                   sqlUpdateNullCheckTemp = _partialProcs["NullCheckForUpdatePartial"];


            string[] temps = { sqlInsertTemp, sqlSelectTemp, sqlSelectTemp, sqlUpdateTemp, sqlDeleteTemp };

            for (int x = 0; x < temps.Length; x++)
            {

                string query = null;

                string inputParams = null,
                       columns = null,
                       values = null,
                       select = null,
                       joins = null,
                       deletes = null,
                       updatesParams = null;

                List<string> inputs = new List<string>(),
                             colm = new List<string>(),
                             val = new List<string>(),
                             sel = new List<string>(),
                             jns = new List<string>(),
                             dels = new List<string>(),
                             updtParams = new List<string>(),
                             innerUpdt = new List<string>();

                if (type.IsClass && (type != typeof(String) || type != typeof(Char)))
                {
                    PropertyInfo[] props = type.GetProperties();

                    for (int i = 0; i < props.Length; i++)
                    {
                        string PK = (props[i].PropertyType.BaseType == typeof(Enum) ? "Id" : props[i].Name);

                        if (i > 0)
                        {
                            inputs.Add("@" + props[i].Name + " " + DeterminSQLType(props[i].PropertyType) + (i == props.Length - 1 ? "" : ","));

                            colm.Add(props[i].Name +
                                (/*(props[i].PropertyType.BaseType.Name == nameof(Enum) || props[i].PropertyType.IsClass) && (props[i].PropertyType.Name != nameof(String) && props[i].PropertyType.Name != nameof(Char))*/
                                ShouldNormalize(props[i].PropertyType) ? "Id" : "") + (i == props.Length - 1 ? "" : ","));

                            val.Add("@" + props[i].Name + (i == props.Length - 1 ? "" : ","));
                        }

                        updtParams.Add("@" + props[i].Name + DeterminSQLType(props[i].PropertyType) + (i == 0 ? "" : " = NULL") + (i == props.Length - 1 ? "" : ","));

                        innerUpdt.Add("SET " + props[i].Name +
                            (/*(props[i].PropertyType.BaseType.Name == nameof(Enum) || props[i].PropertyType.IsClass) && (props[i].PropertyType.Name != nameof(String) && props[i].PropertyType.Name != nameof(Char))*/
                            ShouldNormalize(props[i].PropertyType) ? "Id" : "") + " = @" + props[i].Name + " WHERE " + type.Name + "s." + props[0].Name + " = @" + props[0].Name);


                        if (/*(props[i].PropertyType.BaseType.Name == nameof(Enum) || props[i].PropertyType.IsClass) && (props[i].PropertyType.Name != nameof(String) && props[i].PropertyType.Name != nameof(Char))*/ShouldNormalize(props[i].PropertyType))
                        {
                            jns.Add("Inner Join " + props[i].PropertyType.Name + "s AS " + props[i].Name + "Id On " + props[i].Name + "Id." + (props[i].PropertyType.BaseType == typeof(Enum) ? "Id" : props[i].Name + "Id") + " = " + type.Name + "s." + props[i].Name + "Id");

                            if (!props[i].PropertyType.Namespace.Contains("System") && props[i].PropertyType.BaseType != typeof(Enum))
                            {
                                dels.Add("Delete " + props[i].Name + "s Where " + PK + " = (Select " + PK + " From " + type.Name + " Where " + PK + " = @" + PK + ")");
                            }
                        }

                        sel.Add(type.Name + "s.[" + props[i].Name + (/*(props[i].PropertyType.BaseType.Name == nameof(Enum) || props[i].PropertyType.IsClass) && (props[i].PropertyType.Name != nameof(String) && props[i].PropertyType.Name != nameof(Char))*/
                            ShouldNormalize(props[i].PropertyType) ? "Id" : "") + "]" + (i == props.Length - 1 ? " " : ","));
                    }

                    inputParams = String.Join(" ", inputs.ToArray());
                    columns = String.Join(" ", colm.ToArray());
                    values = String.Join(" ", val.ToArray());
                    select = String.Join(" ", sel.ToArray());
                    joins = String.Join(" ", jns.ToArray());
                    deletes = String.Join(" ", dels.ToArray());
                    updatesParams = String.Join(" ", updtParams.ToArray());

                    if (temps[x] == sqlInsertTemp)
                    {
                        query = String.Format(temps[x], type.Name, inputParams, DeterminSQLType(props[0].PropertyType), type.Name, columns, values);
                    }
                    else if (temps[x] == sqlSelectTemp)
                    {
                        if (x == 1)
                        {
                            query = String.Format(temps[x], type.Name, select, joins, "", "", "All");
                        }
                        else
                        {
                            query = String.Format(temps[x], type.Name, select, joins, "@" + props[0].Name + " " + DeterminSQLType(props[0].PropertyType/*, type.Name*/), "Where " + type.Name + "s." + props[0].Name + " = @" + props[0].Name, "ById");
                        }
                    }
                    else if (temps[x] == sqlUpdateTemp)
                    {

                        string innerQuery = null;
                        for (int q = 1; q < innerUpdt.Count; q++)
                        {
                            innerQuery += String.Format(sqlUpdateNullCheckTemp, type.Name, innerUpdt[q], props[q].Name);
                        }

                        query = String.Format(temps[x], type.Name, "@Id INT, " + inputParams, innerQuery);
                    }
                    else if (temps[x] == sqlDeleteTemp)
                    {

                        query = String.Format(temps[x], type.Name, props[0].Name, DeterminSQLType(props[0].PropertyType/*, type.Name*/), deletes);
                    }


                }
                else if (type.BaseType == typeof(Enum))
                {

                    inputs.Add("@Value " + DeterminSQLType(typeof(string)));
                    updtParams.Add("@Id " + DeterminSQLType(typeof(int)) + ", @Value " + DeterminSQLType(typeof(string)) + " = Null");
                    innerUpdt.Add("SET Value = @Value WHERE " + type.Name + "s.Id = @Id");
                    colm.Add("Value");
                    val.Add("@Value");
                    sel.Add(type.Name + "s.[Id], " + type.Name + "s.[Value]");


                    inputParams = String.Join(" ", inputs.ToArray());
                    columns = String.Join(" ", colm.ToArray());
                    values = String.Join(" ", val.ToArray());
                    select = String.Join(" ", sel.ToArray());
                    joins = String.Join(" ", jns.ToArray());
                    deletes = String.Join(" ", dels.ToArray());
                    updatesParams = String.Join(" ", updtParams.ToArray());

                    if (temps[x] == sqlInsertTemp)
                    {
                        query = String.Format(temps[x], type.Name, inputParams, DeterminSQLType(typeof(int)/*, type.Name*/), type.Name, columns, values);
                    }
                    else if (temps[x] == sqlSelectTemp)
                    {
                        if (x == 1)
                        {
                            query = String.Format(temps[x], type.Name, select, "", "", "", "All");
                        }
                        else
                        {
                            query = String.Format(temps[x], type.Name, select, "", "@Id " + DeterminSQLType(typeof(int)/*, type.Name*/), "Where " + type.Name + "s.Id = @Id", "ById");
                        }
                    }
                    else if (temps[x] == sqlUpdateTemp)
                    {

                        string innerQuery = null;
                        for (int num = 0; num < innerUpdt.Count; num++)
                        {

                            innerQuery += String.Format(sqlUpdateNullCheckTemp, type.Name, innerUpdt[num], "Value");
                        }

                        query = String.Format(temps[x], type.Name, " @Id INT, " + inputParams, innerQuery);
                    }
                    else if (temps[x] == sqlDeleteTemp)
                    {

                        query = String.Format(temps[x], type.Name, "Id", DeterminSQLType(typeof(int)/*, type.Name*/), deletes);
                    }
                }


                DataProvider.ExecuteCmd(() => Connection,
                   query,
                    null,
                    (reader, set) =>
                    {
                        object id = DataMapper<object>.Instance.MapToObject(reader);
                    },
                    null, mod => mod.CommandType = System.Data.CommandType.Text);
            }

            _procedureCreation = false;

        }

        private Dictionary<string, string> CreateTable(Type type)
        {
            _tableCreation = true;

            Dictionary<string, string> result = null;
            List<string> columns = new List<string>();
            List<string> FKs = new List<string>();

            if (CheckIfTableExist(type))
            {
                result = new Dictionary<string, string>();
                result.Add("Name", type.Name + "s");

                if (type.IsClass && (type != typeof(String) || type != typeof(Char)))
                {
                    result.Add("PK", type.GetProperties()[0].Name);
                }
                else if (type.BaseType == typeof(Enum))
                {
                    result.Add("PK", "Id");
                }
                return result;
            }
            else if (type.IsClass && (type != typeof(String) || type != typeof(Char)))
            {
                PropertyInfo[] props = type.GetProperties();
                string endingTable = "NOT NULL, CONSTRAINT [PK_" + type.Name + "s] PRIMARY KEY CLUSTERED ([" + props[0].Name + "] ASC)";


                foreach (var item in props)
                {
                    string FK = null;

                    columns.Add(
                        String.Format(
                            _partialProcs["CreateColumn"],
                           /* ((item.PropertyType.BaseType.Name == nameof(Enum) || item.PropertyType.IsClass) && (item.PropertyType.Name != nameof(String) && item.PropertyType.Name != nameof(Char)))*/ ShouldNormalize(item.PropertyType) ? item.Name + "Id" : item.Name,
                            DeterminSQLType(item.PropertyType),
                            props[0] == item ? "IDENTITY (1, 1) NOT NULL, " : props[props.Length - 1] == item ? endingTable + "," + String.Join(", ", FKs.ToArray()) : "NOT NULL, ")
                        );


                    if (/*item.PropertyType.IsClass && (item.PropertyType.Name != nameof(String) && item.PropertyType.Name != nameof(Char)) || item.PropertyType.BaseType.Name == nameof(Enum)*/ShouldNormalize(item.PropertyType))
                    {
                        Dictionary<string, string> normalizedTbl = CreateTable(item.PropertyType);
                        FK = "CONSTRAINT [FK_" + type.Name + "s_" + item.Name + "] FOREIGN KEY ([" + item.Name + "Id]) REFERENCES [dbo].[" + normalizedTbl["Name"] + "] ([" + normalizedTbl["PK"] + "])";
                        FKs.Add(FK);
                    }
                }
            }
            else if (type.BaseType == typeof(Enum))
            {
                string endingTable = "NOT NULL, CONSTRAINT [PK_" + type.Name + "s] PRIMARY KEY CLUSTERED ([Id] ASC)";
                for (int i = 0; i < 2; i++)
                {
                    columns.Add(
                        String.Format(
                            _partialProcs["CreateColumn"],
                            i == 0 ? "Id" : "Value",
                            DeterminSQLType(i == 0 ? typeof(int) : typeof(string)),
                            i == 0 ? "IDENTITY (1, 1) NOT NULL, " : endingTable)
                     );
                }
            }


            string table = String.Concat(columns.ToArray());
            string query = String.Format(_partialProcs["CreateTable"], type.Name, table);



            int isTrue = 0;
            DataProvider.ExecuteCmd(() => Connection,
               query,
                null,
                (reader, set) =>
                {
                    isTrue = reader.GetSafeInt32(0);
                },
                null,
                mod => mod.CommandType = System.Data.CommandType.Text);


            if (isTrue == 1)
            {
                CreateProcedures(type);
                result = new Dictionary<string, string>();
                result.Add("Name", type.Name + "s");

                if (type.IsClass && (type != typeof(String) || type != typeof(Char)))
                {
                    result.Add("PK", type.GetProperties()[0].Name);
                }
                else if (type.BaseType == typeof(Enum))
                {
                    var fields = type.GetFields();
                    result.Add("PK", "Id");

                    for (int i = 1; i < fields.Length; i++)
                    {
                        DataProvider.ExecuteNonQuery(() => Connection,
                                        "dbo." + type.Name + "s_Insert",
                                        (param) => param.Add(new SqlParameter("Value", fields[i].Name)),
                                        null);
                    }
                }
            }


            _tableCreation = false;
            return result;
        }

        private void UpdateRows(Type type)
        {
            object result = null;
            PropertyInfo[] props = type.GetProperties();
            List<string> columns = new List<string>();

            for (int i = 0; i < props.Length; i++)
            {
                if (i > 0)
                {
                    columns.Add(props[i].Name + (ShouldNormalize(props[i].PropertyType) ? "Id" : "") + (i == props.Length - 1 ? "" : ","));
                }
            }

            string inputs = String.Join(" ", columns.ToArray());

            List<string> oldColumns = GetAllColumns(type);
            string query = _partialProcs["InsertStatement"].FormatString(type.Name, inputs);
            query += _partialProcs["SelectStatement"].FormatString("*") + _partialProcs["FromStatement"].FormatString("temp" + type.Name);

            DataProvider.ExecuteCmd(() => Connection,
               query,
                null,
                (reader, set) =>
                {
                    result = DataMapper<object>.Instance.MapToObject(reader);
                },
                null, mod => mod.CommandType = CommandType.Text);

        }

        #endregion

        public List<T> GetAll()
        {
            List<T> list = null;
            DataProvider.ExecuteCmd(() => Connection, "dbo." + _type.Name + "s_SelectAll",
                null,
                (reader, set) =>
                {
                    T chart = DataMapper<T>.Instance.MapToObject(reader);
                    if (list == null) { list = new List<T>(); }
                    list.Add(chart);
                });
            return list;
        }

        public void Delete(IdType id)
        {
            DataProvider.ExecuteNonQuery(() => Connection, "dbo." + _type.Name + "s_Delete",
                param => param.Add(new SqlParameter(typeof(T).GetProperties()[0].Name, typeof(T).GetProperties()[0].GetValue(id))));
        }

        public T Get(IdType id)
        {
            T chart = default(T);
            DataProvider.ExecuteCmd(() => Connection, "dbo." + _type.Name + "s_SelectById",
                param => param.Add(new SqlParameter(typeof(T).GetProperties()[0].Name, typeof(T).GetProperties()[0].GetValue(id))),
                (reader, set) =>
                {
                    chart = DataMapper<T>.Instance.MapToObject(reader);
                });
            return chart;
        }

        public IdType Insert(T model)
        {
            IdType id = default(IdType);
            DataProvider.ExecuteCmd(() => Connection, "dbo." + _type.Name + "s_Insert",
                       param =>
                       {
                           foreach (var prop in typeof(T).GetProperties())
                           {

                               if (prop.Name != "Id")
                               {
                                   if (prop.PropertyType.BaseType.Name == nameof(Enum))
                                   {
                                       param.Add(new SqlParameter(prop.Name, (int)prop.GetValue(model)));

                                   }
                                   else
                                   {
                                       param.Add(new SqlParameter(prop.Name, prop.GetValue(model)));
                                   }
                               }
                           }
                       },
                      (reader, set) =>
                      {
                          id = DataMapper<IdType>.Instance.MapToObject(reader);
                      });
            return id;
        }

        public void Update(T model)
        {
            DataProvider.ExecuteNonQuery(() => Connection, "dbo." + _type.Name + "s_Update",
                       param =>
                       {
                           param.Add(new SqlParameter(typeof(T).GetProperties()[0].Name, typeof(T).GetProperties()[0].GetValue(model)));
                           foreach (var prop in typeof(T).GetProperties())
                           {
                               param.Add(new SqlParameter(prop.Name, prop.GetValue(model)));
                           }
                       }
                       );
        }

    }
}