# Delivery note Word template

Canonical file: [`delivery_note_patterns_footer.docx`](./delivery_note_patterns_footer.docx)

Runtime still loads the template from **SharePoint** (`Templates/delivery_note_patterns_footer.docx` on each environment’s site), per ADR 0002. This repo copy is the source of truth for content-control tags and for unit tests.

## Content controls (tags)

| Tag | Source |
|-----|--------|
| `AccountName` | Customer name |
| `AccountNumber` | Customer account number |
| `AddressLine1` | Address line 1 |
| `Postcode` | Postal code |
| `City` | City |
| `DeliveryDate` | Order delivery date `dd/MM/yyyy` |
| `RedBasket` | `C` when red basket, else empty |
| `Total` | Order total `N2` (en-GB) |
| `OrderItems` | Repeating section |
| `OrderItemName_PL` | Product display name (PL) |
| `OrderItemName_EN` | Product English name |
| `Quantity` | Line quantity |

After editing the template, copy it to DEV / TEST / PROD SharePoint `Templates/` before relying on document generation in that environment.
