package raven.client.connection;

import org.junit.Test;

import raven.abstractions.data.Constants;
import raven.abstractions.data.IndexQuery;
import raven.abstractions.data.QueryResult;
import raven.abstractions.json.linq.RavenJObject;
import raven.abstractions.json.linq.RavenJValue;
import raven.client.RavenDBAwareTests;
import raven.client.indexes.IndexDefinitionBuilder;
import static org.junit.Assert.assertEquals;
public class WhereEntityIsTest extends RavenDBAwareTests {

  // this test fails sometimes when run in memory == true
  @Test
  public void testCtanQueryUsingMultipleEntities() throws Exception {
    try {
      createDb();
      IDatabaseCommands dbCommands = serverClient.forDatabase(getDbName());

      IndexDefinitionBuilder definitionBuilder = new IndexDefinitionBuilder();

      definitionBuilder.setMap("docs.WhereEntityIs(new string[] { \"Cats\", \"Dogs\" }).Select(a => new { Color = a.Color})");
      dbCommands.putIndex("test", definitionBuilder.toIndexDefinition(convention));

      Cat cat = new Cat();
      cat.setColor("Black");
      cat.setMewing(true);

      Dog dog = new Dog();
      dog.setBarking(false);
      dog.setColor("Black");

      RavenJObject catMeta = new RavenJObject();
      catMeta.add(Constants.RAVEN_ENTITY_NAME, new RavenJValue("Cats"));
      dbCommands.put("cats/1", null, RavenJObject.fromObject(cat), catMeta);

      RavenJObject dogMeta = new RavenJObject();
      dogMeta.add(Constants.RAVEN_ENTITY_NAME, new RavenJValue("Dogs"));
      dbCommands.put("dogs/1", null, RavenJObject.fromObject(dog), dogMeta);

      waitForNonStaleIndexes(dbCommands);

      IndexQuery query = new IndexQuery();
      query.setQuery("Color:Black");

      QueryResult queryResult = dbCommands.query("test", query, null);
      assertEquals(2, queryResult.getResults().size());

    } finally {
      deleteDb();
    }
  }

  public static class Animal {
    private String id;
    private String color;
    public String getId() {
      return id;
    }
    public void setId(String id) {
      this.id = id;
    }
    public String getColor() {
      return color;
    }
    public void setColor(String color) {
      this.color = color;
    }

  }

  public static class Dog extends Animal {
    private boolean barking;

    public boolean isBarking() {
      return barking;
    }

    public void setBarking(boolean barking) {
      this.barking = barking;
    }
  }

  public static class Cat extends Animal {
    private boolean mewing;

    public boolean isMewing() {
      return mewing;
    }

    public void setMewing(boolean mewing) {
      this.mewing = mewing;
    }

  }
}
