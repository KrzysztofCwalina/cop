export class ApiClient {
    private endpoint: string;

    constructor(endpoint: string) {
        this.endpoint = endpoint;
    }

    async getItems(): Promise<Item[]> {
        try {
            const response = await fetch(this.endpoint);
            return response.json();
        } catch (error) {
            console.log("Error fetching items", error);
            return [];
        }
    }

    async deleteItem(id: string): Promise<void> {
        await fetch(`${this.endpoint}/${id}`, { method: "DELETE" });
    }
}

export interface Item {
    id: string;
    name: string;
}

export function formatItem(item: Item): string {
    return `${item.id}: ${item.name}`;
}
