using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sky17.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class StructuredAddress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The 2025 reform removed districts (two-level: province → ward). Keep the old text so
            // existing addresses still read correctly until the customer re-selects province/ward.
            migrationBuilder.Sql(
                "UPDATE Addresses SET Street = Street || ', ' || District " +
                "WHERE District IS NOT NULL AND TRIM(District) <> '';");

            migrationBuilder.DropColumn(
                name: "District",
                table: "Addresses");

            migrationBuilder.AddColumn<string>(
                name: "DeliveryAddressDetail",
                table: "Orders",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeliveryProvinceCode",
                table: "Orders",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeliveryWardCode",
                table: "Orders",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProvinceCode",
                table: "Addresses",
                type: "TEXT",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WardCode",
                table: "Addresses",
                type: "TEXT",
                maxLength: 10,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeliveryAddressDetail",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "DeliveryProvinceCode",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "DeliveryWardCode",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "ProvinceCode",
                table: "Addresses");

            migrationBuilder.DropColumn(
                name: "WardCode",
                table: "Addresses");

            migrationBuilder.AddColumn<string>(
                name: "District",
                table: "Addresses",
                type: "TEXT",
                nullable: true);
        }
    }
}
