export interface IUserService {
  getUser(id: number): User;
}

export interface User {
  id: number;
  name: string;
}

export enum Role {
  Admin,
  Guest,
}

export class UserService implements IUserService {
  getUser(id: number): User {
    return { id, name: "" };
  }
}
