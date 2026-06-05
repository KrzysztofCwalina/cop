/**
 * @name Find public abstract classes
 * @description Finds classes that are both public and abstract
 * @kind problem
 * @problem.severity warning
 */

import csharp

from Class c
where c.ispublic() and
  c.isabstract()
select c, "Class " + c.getName() + " is public and abstract"